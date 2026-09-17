# Network Stream Stability Tester — build and test entry points.
#
# `make` on its own prints the available targets.

BINARY      := bin/streamtest
VERSION     ?= 1.0.0
IMAGE       ?= hyumerin/nsst
GO          ?= go
NPM         ?= npm
BASE_URL    ?= http://127.0.0.1:8080
LDFLAGS     := -s -w -X github.com/nsst/streamtest/internal/config.Version=$(VERSION)

.DEFAULT_GOAL := help
.PHONY: help deps web build build-go run test test-race test-cover bench vet fmt fmt-check \
        lint smoke smoke-full docker docker-run docker-smoke clean all

help: ## Show this help
	@grep -hE '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) \
		| awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-14s\033[0m %s\n", $$1, $$2}'

deps: ## Install frontend and Go dependencies
	cd web && $(NPM) ci --no-audit --no-fund
	$(GO) mod download

web: ## Build the frontend bundle into internal/webui/dist
	cd web && $(NPM) run build

build-go: ## Compile the server (requires a built frontend)
	$(GO) build -trimpath -ldflags "$(LDFLAGS)" -o $(BINARY) ./cmd/server

build: web build-go ## Build the frontend and the server

all: build ## Alias for build

run: build ## Build and run the server on :8080
	HOST=$${HOST:-0.0.0.0} PORT=$${PORT:-8080} ./$(BINARY)

test: ## Run the Go test suite
	$(GO) test ./...

test-race: ## Run the Go test suite under the race detector
	$(GO) test -race -count=1 ./...

test-cover: ## Run tests with a coverage summary
	$(GO) test -coverprofile=coverage.out ./...
	$(GO) tool cover -func=coverage.out | tail -n 1

bench: ## Run benchmarks
	$(GO) test -bench=. -benchmem ./...

vet: ## Run go vet
	$(GO) vet ./...

fmt: ## Format Go sources
	gofmt -w .

fmt-check: ## Fail if any Go source is not gofmt-clean
	@out=$$(gofmt -l .); \
	if [ -n "$$out" ]; then echo "not gofmt-clean:"; echo "$$out"; exit 1; fi

lint: fmt-check vet ## Format check plus go vet

# Verifies the three streaming protocols against a server that is already
# running. The frame counts are exact: duration/interval frames are emitted.
smoke: ## Smoke test a running server (override BASE_URL=...)
	@set -e; \
	base="$(BASE_URL)"; \
	printf 'health           : '; curl -fsS "$$base/api/health"; echo; \
	printf 'info protocols   : '; curl -fsS "$$base/api/info" | tr ',' '\n' | grep -c 'stream\|sse\|websocket'; \
	printf 'ndjson frames    : '; curl -fsS -N "$$base/api/stream/http?duration=1&interval=250&payload_size=16" | grep -c '^'; \
	printf 'sse events       : '; curl -fsS -N "$$base/api/stream/sse?duration=1&interval=250&payload_size=0" | grep -c '^data:'; \
	printf 'bad param status : '; curl -s -o /dev/null -w '%{http_code}\n' "$$base/api/stream/http?interval=1"; \
	printf 'unknown api      : '; curl -s -o /dev/null -w '%{http_code}\n' "$$base/api/nope"; \
	printf 'console status   : '; curl -s -o /dev/null -w '%{http_code}\n' "$$base/"

smoke-full: ## Smoke test including the WebSocket endpoint (requires the Go client)
	$(GO) test -count=1 -run 'TestWebSocket' ./internal/websocket/...

docker: ## Build the container image
	docker build --build-arg VERSION=$(VERSION) -t $(IMAGE) .

docker-run: ## Run the container on :8080
	docker run --rm -p 8080:8080 --name streamtest $(IMAGE)

docker-smoke: ## Build, run detached, smoke test, stop
	@set -e; \
	docker build --build-arg VERSION=$(VERSION) -t $(IMAGE) .; \
	docker rm -f streamtest >/dev/null 2>&1 || true; \
	docker run -d --name streamtest -p 8080:8080 $(IMAGE) >/dev/null; \
	trap 'docker rm -f streamtest >/dev/null 2>&1 || true' EXIT; \
	for i in $$(seq 1 30); do \
		if curl -fsS "$(BASE_URL)/api/health" >/dev/null 2>&1; then break; fi; \
		sleep 1; \
	done; \
	$(MAKE) smoke

clean: ## Remove build output
	rm -rf bin coverage.out internal/webui/dist
	rm -rf web/node_modules web/dist
