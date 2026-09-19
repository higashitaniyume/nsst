# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Network Stream Stability Tester
#
# Three stages so that neither Node.js nor the .NET SDK ends up in the
# published image:
#
#   web     -> builds the TypeScript console with Vite, into web/dist
#   build   -> publishes the ASP.NET server, embedding the console into it
#   runtime -> carries only the ASP.NET runtime and the published output
#
# The console is embedded as an assembly resource, so the final image serves the
# UI, the REST API and all three stream endpoints from one process.
#
# Multi-architecture builds:
#   The web and build stages are pinned to $BUILDPLATFORM. Both produce
#   platform-independent output — a JS bundle, and a framework-dependent .NET
#   publish (IL plus a deps.json), which is the same bytes on every platform —
#   so they run once, natively, and never under QEMU. The runtime stage is left
#   on the target platform, so the published manifest is a genuine multi-arch
#   image.
#
#   This is why the build stage does NOT pass -r/--runtime: a RID-specific
#   publish would pin the output to one architecture and force the compiler to
#   run per-architecture. Without it, `dotnet Nsst.Server.dll` starts on any
#   platform whose runtime image is present.
# ---------------------------------------------------------------------------

# --- Stage 1: build the web console ----------------------------------------
FROM --platform=${BUILDPLATFORM} node:22-alpine AS web

WORKDIR /src/web

# Dependencies are installed before the sources are copied so that editing the
# frontend does not invalidate the npm layer.
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY web/ ./

# Vite writes to web/dist (its outDir). This is the only source of the console:
# .dockerignore excludes any host-built copy, and the bundle is gitignored.
RUN npm run build


# --- Stage 2: publish the server -------------------------------------------
FROM --platform=${BUILDPLATFORM} mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

WORKDIR /src

# NuGet packages are restored before the sources are copied, so editing C# does
# not invalidate the restore layer. The package cache is a BuildKit cache mount
# rather than a layer, so it survives a source change.
COPY dotnet/Directory.Build.props ./dotnet/
COPY dotnet/src/Nsst.Core/Nsst.Core.csproj ./dotnet/src/Nsst.Core/
COPY dotnet/src/Nsst.Server/Nsst.Server.csproj ./dotnet/src/Nsst.Server/
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore ./dotnet/src/Nsst.Server/Nsst.Server.csproj

COPY dotnet/ ./dotnet/

# The console is embedded by Nsst.Server.csproj from ../../../web/dist, relative
# to the project file, so it has to be in place before publish.
COPY --from=web /src/web/dist ./web/dist

ARG VERSION=0.3.0

# The default above mirrors <Version> in dotnet/Directory.Build.props, which is the
# canonical value; this only applies to a bare `docker build` that passes no
# --build-arg. Compose and the Makefile both pass it explicitly, so in normal use
# there is exactly one source of the number.
#
# The version is stamped into the assembly rather than written in source:
# ServerConfig reads AssemblyInformationalVersion, which is what /api/health and
# /api/info report.
#
# UseAppHost=false suppresses the native launcher. It would otherwise be built for
# $BUILDPLATFORM (amd64) and then copied into a possibly arm64 runtime image — an
# executable of the wrong architecture sitting in the image, which `.NET` ignores
# but a human would trip over. The entrypoint runs the DLL through `dotnet` anyway.
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish ./dotnet/src/Nsst.Server/Nsst.Server.csproj \
        -c Release \
        --no-restore \
        -p:Version=${VERSION} \
        -p:UseAppHost=false \
        -o /out


# --- Stage 3: runtime ------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime

# A dedicated unprivileged account; the service never needs to write to disk.
# The base image already defines an "app" user, but an explicit uid keeps the
# numeric owner stable across base-image updates.
RUN addgroup -S -g 10001 nsst \
 && adduser -S -u 10001 -G nsst -H -s /sbin/nologin nsst

WORKDIR /app
COPY --from=build /out/ ./

LABEL org.opencontainers.image.title="Network Stream Stability Tester" \
      org.opencontainers.image.description="Long-lived HTTP streaming, SSE and WebSocket stability probe" \
      org.opencontainers.image.source="https://github.com/higashitaniyume/nsst" \
      org.opencontainers.image.licenses="MIT"

ENV HOST=0.0.0.0 \
    PORT=8080

USER 10001:10001
EXPOSE 8080

# busybox wget ships with the Alpine base, so the healthcheck needs no extra
# package. Deliberately no GC or memory env vars here: the right heap setting
# depends on measurements that have not been taken yet, and guessing one would
# cap the server on a large host for no reason.
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD wget -q -O /dev/null "http://127.0.0.1:${PORT}/api/health" || exit 1

# The server handles SIGTERM by draining live streams before it exits; Docker
# must not kill it early, hence the matching stop grace period in compose.
STOPSIGNAL SIGTERM

ENTRYPOINT ["dotnet", "Nsst.Server.dll"]
