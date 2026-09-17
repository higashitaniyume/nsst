// Package httpx contains small HTTP helpers shared by the API layer and the
// streaming handlers: JSON rendering, consistent error bodies, CORS and panic
// recovery middleware.
package httpx

import (
	"encoding/json"
	"errors"
	"log/slog"
	"net/http"
	"strings"

	"github.com/nsst/streamtest/internal/stream"
)

// ErrorBody is the JSON body returned for every non-2xx API response.
type ErrorBody struct {
	Error   string `json:"error"`
	Message string `json:"message"`
	Param   string `json:"param,omitempty"`
}

// Error codes used by the API.
const (
	CodeInvalidParameter = "invalid_parameter"
	CodeTooManyStreams   = "too_many_streams"
	CodeShuttingDown     = "server_shutting_down"
	CodeNotFound         = "not_found"
	CodeMethodNotAllowed = "method_not_allowed"
	CodeInternal         = "internal_error"
)

// WriteJSON renders v as JSON with the given status code.
func WriteJSON(w http.ResponseWriter, status int, v any) {
	body, err := json.Marshal(v)
	if err != nil {
		// v is always a local, marshalable value; treat a failure as a bug.
		slog.Default().Error("httpx: marshal response", "error", err)
		http.Error(w, `{"error":"internal_error","message":"failed to encode response"}`, http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Content-Length", itoa(len(body)))
	w.WriteHeader(status)
	_, _ = w.Write(body)
}

// WriteError renders a structured error body.
func WriteError(w http.ResponseWriter, status int, code, message, param string) {
	WriteJSON(w, status, ErrorBody{Error: code, Message: message, Param: param})
}

// WriteAcquireError maps a stream.Manager.Acquire failure onto an HTTP response.
func WriteAcquireError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, stream.ErrTooManyStreams):
		w.Header().Set("Retry-After", "5")
		WriteError(w, http.StatusServiceUnavailable, CodeTooManyStreams, err.Error(), "")
	case errors.Is(err, stream.ErrServerShutdown):
		w.Header().Set("Retry-After", "10")
		WriteError(w, http.StatusServiceUnavailable, CodeShuttingDown, err.Error(), "")
	default:
		// The client is already gone; the status is informational only.
		WriteError(w, http.StatusInternalServerError, CodeInternal, err.Error(), "")
	}
}

func itoa(n int) string {
	if n == 0 {
		return "0"
	}
	var buf [20]byte
	i := len(buf)
	for n > 0 {
		i--
		buf[i] = byte('0' + n%10)
		n /= 10
	}
	return string(buf[i:])
}

// CORS implements a deliberately small cross origin policy.
//
// The zero value (no configured origins) emits no CORS headers at all, which
// means browsers only allow same origin requests. "*" opts in to unrestricted
// cross origin access; anything else is an exact origin allow list.
type CORS struct {
	allowAll bool
	origins  map[string]struct{}
}

// NewCORS builds a policy from a list of origins. Blank entries are ignored.
func NewCORS(origins []string) CORS {
	c := CORS{origins: make(map[string]struct{}, len(origins))}
	for _, o := range origins {
		o = strings.TrimSpace(o)
		if o == "" {
			continue
		}
		if o == "*" {
			c.allowAll = true
			continue
		}
		c.origins[o] = struct{}{}
	}
	return c
}

// AllowAll reports whether every origin is accepted.
func (c CORS) AllowAll() bool { return c.allowAll }

// Empty reports whether no cross origin access is configured.
func (c CORS) Empty() bool { return !c.allowAll && len(c.origins) == 0 }

// OriginPatterns returns the host patterns accepted by the WebSocket upgrader.
// It returns nil for the default same-origin-only policy.
func (c CORS) OriginPatterns() []string {
	if c.allowAll || len(c.origins) == 0 {
		return nil
	}
	patterns := make([]string, 0, len(c.origins))
	for o := range c.origins {
		p := o
		if i := strings.Index(p, "://"); i >= 0 {
			p = p[i+3:]
		}
		if i := strings.IndexAny(p, "/?"); i >= 0 {
			p = p[:i]
		}
		if p != "" {
			patterns = append(patterns, p)
		}
	}
	return patterns
}

// Allows reports whether an Origin header value is allowed.
func (c CORS) Allows(origin string) bool {
	if origin == "" {
		return false
	}
	if c.allowAll {
		return true
	}
	_, ok := c.origins[origin]
	return ok
}

const corsMaxAge = "600"

// Middleware answers preflight requests and decorates actual responses with the
// appropriate Access-Control-* headers.
func (c CORS) Middleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		origin := r.Header.Get("Origin")
		if origin != "" {
			w.Header().Add("Vary", "Origin")
			if c.allowAll {
				w.Header().Set("Access-Control-Allow-Origin", "*")
			} else if c.Allows(origin) {
				w.Header().Set("Access-Control-Allow-Origin", origin)
				w.Header().Set("Access-Control-Allow-Credentials", "false")
			}
		}

		if r.Method == http.MethodOptions && r.Header.Get("Access-Control-Request-Method") != "" {
			w.Header().Add("Vary", "Access-Control-Request-Method")
			w.Header().Add("Vary", "Access-Control-Request-Headers")
			if origin == "" || (!c.allowAll && !c.Allows(origin)) {
				w.WriteHeader(http.StatusNoContent)
				return
			}
			w.Header().Set("Access-Control-Allow-Methods", "GET, HEAD, OPTIONS")
			reqHeaders := r.Header.Get("Access-Control-Request-Headers")
			if reqHeaders == "" {
				reqHeaders = "Content-Type"
			}
			w.Header().Set("Access-Control-Allow-Headers", reqHeaders)
			w.Header().Set("Access-Control-Max-Age", corsMaxAge)
			w.WriteHeader(http.StatusNoContent)
			return
		}

		next.ServeHTTP(w, r)
	})
}

// statusRecorder tracks whether the response header has been sent so that the
// panic recoverer knows whether it may still write an error body. It implements
// Unwrap so that http.ResponseController keeps working (Flush, write deadlines).
type statusRecorder struct {
	http.ResponseWriter
	status      int
	wroteHeader bool
}

func (s *statusRecorder) WriteHeader(status int) {
	if s.wroteHeader {
		return
	}
	s.status = status
	s.wroteHeader = true
	s.ResponseWriter.WriteHeader(status)
}

func (s *statusRecorder) Write(b []byte) (int, error) {
	if !s.wroteHeader {
		s.WriteHeader(http.StatusOK)
	}
	return s.ResponseWriter.Write(b)
}

// Unwrap lets http.ResponseController reach the underlying writer.
func (s *statusRecorder) Unwrap() http.ResponseWriter { return s.ResponseWriter }

// Recoverer converts a panic inside a handler into a 500 response (when the
// response has not started yet) and a structured log entry.
func Recoverer(logger *slog.Logger, next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		rec := &statusRecorder{ResponseWriter: w, status: http.StatusOK}
		defer func() {
			rv := recover()
			if rv == nil {
				return
			}
			// net/http uses ErrAbortHandler to silently drop a connection; the
			// server handles that itself, so let it propagate.
			if rv == http.ErrAbortHandler { //nolint:errorlint // sentinel comparison is intended
				panic(rv)
			}
			logger.Error("panic recovered",
				"panic", rv,
				"method", r.Method,
				"path", r.URL.Path,
				"remote", r.RemoteAddr,
			)
			if !rec.wroteHeader {
				WriteError(rec, http.StatusInternalServerError, CodeInternal, "internal server error", "")
			}
		}()
		next.ServeHTTP(rec, r)
	})
}
