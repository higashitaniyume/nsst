# Network Stream Stability Tester — build and test entry points.
#
# `make` on its own prints the available targets.
#
# These targets use POSIX shell (grep/awk/seq/find). On Windows run them from WSL
# or Git Bash, or use the dotnet CLI directly — every target is a thin wrapper.

PUBLISH_DIR := bin/publish
ASSEMBLY    := Nsst.Server.dll
SOLUTION    := dotnet/Nsst.slnx
PROJECT     := dotnet/src/Nsst.Server/Nsst.Server.csproj
VERSION     ?= 0.2.2
IMAGE       ?= hyumerin/nsst
DOTNET      ?= dotnet
NPM         ?= npm
BASE_URL    ?= http://127.0.0.1:8080

.DEFAULT_GOAL := help
.PHONY: help deps web build run test test-cover fmt fmt-check lint smoke smoke-full \
        docker docker-run docker-smoke clean all

help: ## Show this help
	@grep -hE '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) \
		| awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-14s\033[0m %s\n", $$1, $$2}'

deps: ## Install frontend and NuGet dependencies
	cd web && $(NPM) ci --no-audit --no-fund
	$(DOTNET) restore $(SOLUTION)

web: ## Build the console into web/dist
	cd web && $(NPM) run build

# The console is embedded as an assembly resource, so the frontend has to be
# built first. Publishing from a clean checkout without it produces a server with
# no UI — which is why this target depends on `web` rather than documenting it.
build: web ## Publish the server, embedding the console (requires a built frontend)
	$(DOTNET) publish $(PROJECT) -c Release -p:Version=$(VERSION) -o $(PUBLISH_DIR)

all: build ## Alias for build

run: build ## Build and run the server on :8080
	HOST=$${HOST:-0.0.0.0} PORT=$${PORT:-8080} $(DOTNET) $(PUBLISH_DIR)/$(ASSEMBLY)

test: ## Run the test suite
	$(DOTNET) test $(SOLUTION) -c Release --nologo

test-cover: ## Run tests with a coverage summary
	$(DOTNET) test $(SOLUTION) -c Release --nologo \
		--collect:"XPlat Code Coverage" --results-directory $(PUBLISH_DIR)/coverage

fmt: ## Format C# sources
	$(DOTNET) format $(SOLUTION)

fmt-check: ## Fail if any C# source is not formatted
	$(DOTNET) format $(SOLUTION) --verify-no-changes

# TreatWarningsAsErrors is on in Directory.Build.props, so a successful build is
# the static-analysis gate; there is no separate linter to run.
lint: fmt-check ## Format check, then a warnings-as-errors build
	$(DOTNET) build $(SOLUTION) -c Release --nologo

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

# The WebSocket endpoint covers the one protocol curl cannot exercise, so it is
# checked by the suite rather than by the shell smoke test above.
smoke-full: smoke ## Smoke test plus the full suite (covers WebSocket)
	$(DOTNET) test $(SOLUTION) -c Release --nologo

docker: ## Build the container image
	docker build --build-arg VERSION=$(VERSION) -t $(IMAGE):$(VERSION) -t $(IMAGE):latest .

docker-run: ## Run the container on :8080
	docker run --rm -p 8080:8080 --name streamtest $(IMAGE):$(VERSION)

docker-smoke: ## Build, run detached, smoke test, stop
	@set -e; \
	docker build --build-arg VERSION=$(VERSION) -t $(IMAGE):$(VERSION) .; \
	docker rm -f streamtest >/dev/null 2>&1 || true; \
	docker run -d --name streamtest -p 8080:8080 $(IMAGE):$(VERSION) >/dev/null; \
	trap 'docker rm -f streamtest >/dev/null 2>&1 || true' EXIT; \
	for i in $$(seq 1 30); do \
		if curl -fsS "$(BASE_URL)/api/health" >/dev/null 2>&1; then break; fi; \
		sleep 1; \
	done; \
	$(MAKE) smoke

clean: ## Remove build output
	rm -rf bin web/dist web/node_modules
	find dotnet -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
