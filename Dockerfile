# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Network Stream Stability Tester
#
# Three stages so that neither Node.js nor the Go toolchain ends up in the
# published image:
#
#   web     -> builds the TypeScript console with Vite
#   build   -> compiles a static Go binary and embeds the console into it
#   runtime -> carries nothing but the binary and a non-root user
#
# The console is compiled into the binary with go:embed, so the final image
# serves the UI, the REST API and all three stream endpoints from one process.
#
# Multi-architecture builds:
#   The web and build stages are pinned to $BUILDPLATFORM rather than the
#   target platform. Both produce platform-independent output (a JS bundle and
#   a CGO-free static binary), so they run once, natively, and the Go toolchain
#   cross-compiles to $TARGETARCH. Building linux/amd64 and linux/arm64
#   therefore does not run npm or the Go compiler under QEMU emulation at all.
#   The runtime stage is left on the target platform, so the published manifest
#   is still a genuine multi-arch image.
# ---------------------------------------------------------------------------

# --- Stage 1: build the web console ----------------------------------------
FROM --platform=${BUILDPLATFORM} node:22-alpine AS web

WORKDIR /src/web

# Dependencies are installed before the sources are copied so that editing the
# frontend does not invalidate the npm layer.
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY web/ ./

# Vite is configured to write straight into the Go package that embeds it, so
# this populates /src/internal/webui/dist.
RUN npm run build


# --- Stage 2: compile the server -------------------------------------------
FROM --platform=${BUILDPLATFORM} golang:1.24-alpine AS build

WORKDIR /src

# Resolve modules first: this layer is cached until go.mod/go.sum change. The
# module and build caches are BuildKit cache mounts, so they persist across
# builds instead of living in a layer that a source edit throws away.
COPY go.mod go.sum ./
RUN --mount=type=cache,target=/go/pkg/mod \
    --mount=type=cache,target=/root/.cache/go-build \
    go mod download

COPY cmd/ ./cmd/
COPY internal/ ./internal/
COPY pkg/ ./pkg/

# The checked-in bundle is replaced by the one just built from web/ sources. The
# build context excludes internal/webui/dist, so this is the only source of the UI.
RUN rm -rf ./internal/webui/dist
COPY --from=web /src/internal/webui/dist ./internal/webui/dist

# Supplied by BuildKit for each platform in the build.
ARG TARGETOS
ARG TARGETARCH
ARG VERSION=1.0.0

# CGO_ENABLED=0 produces a static binary that runs on a bare Alpine base, and is
# what makes the cross-compilation above possible.
RUN --mount=type=cache,target=/go/pkg/mod \
    --mount=type=cache,target=/root/.cache/go-build \
    CGO_ENABLED=0 GOOS=${TARGETOS} GOARCH=${TARGETARCH} go build \
        -trimpath \
        -ldflags "-s -w -X github.com/nsst/streamtest/internal/config.Version=${VERSION}" \
        -o /out/streamtest \
        ./cmd/server


# --- Stage 3: runtime ------------------------------------------------------
FROM alpine:3.21 AS runtime

# A dedicated unprivileged account; the service never needs to write to disk.
RUN addgroup -S -g 10001 streamtest \
 && adduser -S -u 10001 -G streamtest -H -s /sbin/nologin streamtest

COPY --from=build /out/streamtest /usr/local/bin/streamtest

LABEL org.opencontainers.image.title="Network Stream Stability Tester" \
      org.opencontainers.image.description="Long-lived HTTP streaming, SSE and WebSocket stability probe" \
      org.opencontainers.image.source="https://github.com/higashitaniyume/nsst" \
      org.opencontainers.image.licenses="MIT"

ENV HOST=0.0.0.0 \
    PORT=8080

USER 10001:10001
EXPOSE 8080

# busybox wget is part of the Alpine base, so the healthcheck needs no extras.
HEALTHCHECK --interval=30s --timeout=3s --start-period=3s --retries=3 \
    CMD wget -q -O /dev/null "http://127.0.0.1:${PORT}/api/health" || exit 1

# The server handles SIGTERM by draining live streams before it exits; Docker
# must not kill it early, hence the matching stop grace period in compose.yaml.
STOPSIGNAL SIGTERM

ENTRYPOINT ["/usr/local/bin/streamtest"]
