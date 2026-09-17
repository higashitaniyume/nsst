// Package testsupport builds a fully wired test server. Tests use it so that they
// exercise the real router, middleware, limits and lifecycle manager rather than
// hand assembled handlers.
package testsupport

import (
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"github.com/nsst/streamtest/internal/api"
	"github.com/nsst/streamtest/internal/config"
	"github.com/nsst/streamtest/internal/httpx"
	"github.com/nsst/streamtest/internal/metrics"
	"github.com/nsst/streamtest/internal/stream"
)

// Stack is an httptest server running the production router.
type Stack struct {
	Server   *httptest.Server
	Manager  *stream.Manager
	Counters *metrics.Counters
	Limits   stream.Limits
}

// FastLimits returns limits tuned for tests: short durations and small payloads
// so the suite stays fast while still covering every code path.
func FastLimits() stream.Limits {
	l := stream.DefaultLimits()
	l.MinDuration = time.Second
	l.MaxDuration = 5 * time.Second
	l.DefaultDuration = time.Second
	l.MinInterval = 10 * time.Millisecond
	l.MaxInterval = time.Second
	l.DefaultInterval = 100 * time.Millisecond
	l.MaxPayloadSize = 64 << 10
	l.DefaultPayloadSize = 128
	l.MaxConcurrentStreams = 16
	l.WriteTimeout = 5 * time.Second
	return l
}

// New starts a test server.
func New(lim stream.Limits, origins []string) *Stack {
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))
	counters := metrics.New()
	manager := stream.NewManager(lim, counters)

	handler := api.New(api.Options{
		Version:  config.Version,
		Name:     config.Name,
		Limits:   lim,
		Manager:  manager,
		Counters: counters,
		Logger:   logger,
		CORS:     httpx.NewCORS(origins),
		Static:   http.NotFoundHandler(),
	})

	return &Stack{
		Server:   httptest.NewServer(handler),
		Manager:  manager,
		Counters: counters,
		Limits:   lim,
	}
}

// Close drains running streams and stops the server.
func (s *Stack) Close() {
	s.Manager.StartDraining()
	s.Manager.ForceClose()
	s.Server.Close()
}

// URL builds an absolute URL for a path on the test server.
func (s *Stack) URL(path string) string { return s.Server.URL + path }

// WSURL builds an absolute ws:// URL for a path on the test server.
func (s *Stack) WSURL(path string) string {
	return "ws" + s.Server.URL[len("http"):] + path
}

// WaitFor polls cond until it is true or the timeout expires, failing the test.
func WaitFor(t *testing.T, what string, timeout time.Duration, cond func() bool) {
	t.Helper()
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		if cond() {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatalf("timed out after %s waiting for %s", timeout, what)
}
