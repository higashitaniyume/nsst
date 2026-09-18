// Package api wires the public HTTP surface: the REST endpoints, the three
// streaming protocols and the embedded front end.
package api

import (
	"log/slog"
	"net/http"
	"strings"
	"time"

	"github.com/nsst/streamtest/internal/config"
	"github.com/nsst/streamtest/internal/httpstream"
	"github.com/nsst/streamtest/internal/httpx"
	"github.com/nsst/streamtest/internal/metrics"
	"github.com/nsst/streamtest/internal/sse"
	"github.com/nsst/streamtest/internal/stream"
	"github.com/nsst/streamtest/internal/websocket"
)

// Protocols is the canonical protocol identifier list.
var Protocols = []string{httpstream.Protocol, sse.Protocol, websocket.Protocol}

// Options configures the router.
type Options struct {
	Version  string
	Name     string
	Limits   stream.Limits
	Manager  *stream.Manager
	Counters *metrics.Counters
	Logger   *slog.Logger
	CORS     httpx.CORS
	// Static serves the embedded front end.
	Static http.Handler
}

// API is the assembled HTTP handler.
type API struct {
	opts      Options
	startedAt time.Time
	router    http.Handler
}

// New builds the API router.
func New(opts Options) *API {
	if opts.Logger == nil {
		opts.Logger = slog.Default()
	}
	if opts.Counters == nil {
		opts.Counters = metrics.New()
	}

	a := &API{opts: opts, startedAt: time.Now()}

	mux := http.NewServeMux()

	// REST surface.
	mux.HandleFunc("GET /api/health", a.handleHealth)
	mux.HandleFunc("GET /api/info", a.handleInfo)
	mux.HandleFunc("GET /api/config", a.handleConfig)
	mux.HandleFunc("GET /api/metrics", a.handleMetrics)
	mux.HandleFunc("GET /api/client", a.handleClient)

	// The three streaming protocols.
	mux.Handle("GET /api/stream/http",
		httpstream.New(opts.Limits, opts.Manager, opts.Counters, opts.Logger))
	mux.Handle("GET /api/stream/sse",
		sse.New(opts.Limits, opts.Manager, opts.Counters, opts.Logger))
	mux.Handle("GET /api/stream/ws",
		websocket.New(opts.Limits, opts.Manager, opts.Counters, opts.Logger, opts.CORS))

	// Unknown /api paths must never fall through to the SPA. This pattern is less
	// specific than the method qualified ones above, so it only sees leftovers.
	mux.HandleFunc("/api/", a.handleUnknownAPI)

	// Front end.
	static := opts.Static
	if static == nil {
		static = http.NotFoundHandler()
	}
	mux.Handle("/", static)

	// Order: recover (outermost) -> CORS -> routes.
	a.router = httpx.Recoverer(opts.Logger, opts.CORS.Middleware(mux))
	return a
}

// ServeHTTP implements http.Handler.
func (a *API) ServeHTTP(w http.ResponseWriter, r *http.Request) { a.router.ServeHTTP(w, r) }

// Uptime returns how long the process has been serving.
func (a *API) Uptime() time.Duration { return time.Since(a.startedAt) }

// ---------------------------------------------------------------- REST models

type limitsView struct {
	MaxDuration          int64 `json:"max_duration"`
	MinDuration          int64 `json:"min_duration"`
	MaxInterval          int64 `json:"max_interval"`
	MinInterval          int64 `json:"min_interval"`
	MaxPayloadSize       int   `json:"max_payload_size"`
	MaxConcurrentStreams int   `json:"max_concurrent_streams"`
	WriteTimeoutMS       int64 `json:"write_timeout_ms"`
}

type defaultsView struct {
	Duration    int64 `json:"duration"`
	Interval    int64 `json:"interval"`
	PayloadSize int   `json:"payload_size"`
}

type healthResponse struct {
	Status     string `json:"status"`
	Version    string `json:"version"`
	UptimeSecs int64  `json:"uptime_seconds"`
}

type infoResponse struct {
	Name          string            `json:"name"`
	Version       string            `json:"version"`
	Protocols     []string          `json:"protocols"`
	Endpoints     map[string]string `json:"endpoints"`
	UptimeSecs    int64             `json:"uptime_seconds"`
	ActiveStreams int64             `json:"active_streams"`
	Limits        limitsView        `json:"limits"`
	Defaults      defaultsView      `json:"defaults"`
}

type configResponse struct {
	Name      string            `json:"name"`
	Version   string            `json:"version"`
	Protocols []string          `json:"protocols"`
	Endpoints map[string]string `json:"endpoints"`
	Limits    limitsView        `json:"limits"`
	Defaults  defaultsView      `json:"defaults"`
}

// ------------------------------------------------------------------- handlers

func (a *API) handleHealth(w http.ResponseWriter, r *http.Request) {
	httpx.WriteJSON(w, http.StatusOK, healthResponse{
		Status:     "ok",
		Version:    a.opts.Version,
		UptimeSecs: int64(a.Uptime().Seconds()),
	})
}

func (a *API) handleInfo(w http.ResponseWriter, r *http.Request) {
	httpx.WriteJSON(w, http.StatusOK, infoResponse{
		Name:          a.opts.Name,
		Version:       a.opts.Version,
		Protocols:     Protocols,
		Endpoints:     endpoints(),
		UptimeSecs:    int64(a.Uptime().Seconds()),
		ActiveStreams: a.opts.Counters.ActiveStreams.Load(),
		Limits:        a.limitsView(),
		Defaults:      a.defaultsView(),
	})
}

func (a *API) handleConfig(w http.ResponseWriter, r *http.Request) {
	httpx.WriteJSON(w, http.StatusOK, configResponse{
		Name:      a.opts.Name,
		Version:   a.opts.Version,
		Protocols: Protocols,
		Endpoints: endpoints(),
		Limits:    a.limitsView(),
		Defaults:  a.defaultsView(),
	})
}

func (a *API) handleMetrics(w http.ResponseWriter, r *http.Request) {
	httpx.WriteJSON(w, http.StatusOK, a.opts.Counters.Snapshot())
}

// handleUnknownAPI answers requests under /api/ that no route matched. Known
// paths reached with the wrong method get 405 rather than a confusing 404.
func (a *API) handleUnknownAPI(w http.ResponseWriter, r *http.Request) {
	clean := strings.TrimSuffix(r.URL.Path, "/")
	if methods, ok := knownRoutes[clean]; ok {
		w.Header().Set("Allow", methods)
		httpx.WriteError(w, http.StatusMethodNotAllowed, httpx.CodeMethodNotAllowed,
			"method "+r.Method+" is not allowed for "+clean, "")
		return
	}
	httpx.WriteError(w, http.StatusNotFound, httpx.CodeNotFound, "no such endpoint: "+r.URL.Path, "")
}

// -------------------------------------------------------------------- helpers

var knownRoutes = map[string]string{
	"/api/health":      "GET, HEAD, OPTIONS",
	"/api/info":        "GET, HEAD, OPTIONS",
	"/api/config":      "GET, HEAD, OPTIONS",
	"/api/metrics":     "GET, HEAD, OPTIONS",
	"/api/client":      "GET, HEAD, OPTIONS",
	"/api/stream/http": "GET, OPTIONS",
	"/api/stream/sse":  "GET, OPTIONS",
	"/api/stream/ws":   "GET, OPTIONS",
}

func endpoints() map[string]string {
	return map[string]string{
		"health":    "/api/health",
		"info":      "/api/info",
		"config":    "/api/config",
		"metrics":   "/api/metrics",
		"client":    "/api/client",
		"http":      "/api/stream/http",
		"sse":       "/api/stream/sse",
		"websocket": "/api/stream/ws",
	}
}

func (a *API) limitsView() limitsView {
	l := a.opts.Limits
	return limitsView{
		MaxDuration:          int64(l.MaxDuration / time.Second),
		MinDuration:          int64(l.MinDuration / time.Second),
		MaxInterval:          int64(l.MaxInterval / time.Millisecond),
		MinInterval:          int64(l.MinInterval / time.Millisecond),
		MaxPayloadSize:       l.MaxPayloadSize,
		MaxConcurrentStreams: l.MaxConcurrentStreams,
		WriteTimeoutMS:       int64(l.WriteTimeout / time.Millisecond),
	}
}

func (a *API) defaultsView() defaultsView {
	l := a.opts.Limits
	return defaultsView{
		Duration:    int64(l.DefaultDuration / time.Second),
		Interval:    int64(l.DefaultInterval / time.Millisecond),
		PayloadSize: l.DefaultPayloadSize,
	}
}

// Version is re-exported for callers that only import this package. It is a
// variable because config.Version can be stamped in at link time.
var Version = config.Version
