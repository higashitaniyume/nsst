// Package httpstream implements GET /api/stream/http, an NDJSON streaming
// endpoint: one JSON encoded frame per line, flushed after every frame.
package httpstream

import (
	"context"
	"errors"
	"log/slog"
	"net/http"
	"time"

	"github.com/nsst/streamtest/internal/httpx"
	"github.com/nsst/streamtest/internal/metrics"
	"github.com/nsst/streamtest/internal/stream"
	"github.com/nsst/streamtest/pkg/protocol"
)

// ContentType is the media type of the NDJSON stream.
const ContentType = "application/x-ndjson"

// Protocol is the identifier reported in logs and by /api/info.
const Protocol = "http-stream"

var newline = []byte{'\n'}

// Handler serves the NDJSON streaming endpoint.
type Handler struct {
	Limits   stream.Limits
	Manager  *stream.Manager
	Counters *metrics.Counters
	Logger   *slog.Logger
}

// New builds a Handler.
func New(lim stream.Limits, mgr *stream.Manager, counters *metrics.Counters, logger *slog.Logger) *Handler {
	return &Handler{Limits: lim, Manager: mgr, Counters: counters, Logger: logger}
}

// ServeHTTP implements http.Handler.
func (h *Handler) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	params, err := stream.ParseParams(r.URL.Query(), h.Limits)
	if err != nil {
		var pe *stream.ParamError
		if errors.As(err, &pe) {
			httpx.WriteError(w, http.StatusBadRequest, httpx.CodeInvalidParameter, pe.Error(), pe.Param)
			return
		}
		httpx.WriteError(w, http.StatusBadRequest, httpx.CodeInvalidParameter, err.Error(), "")
		return
	}

	lease, err := h.Manager.Acquire(r.Context())
	if err != nil {
		httpx.WriteAcquireError(w, err)
		return
	}
	defer lease.Release()

	w.Header().Set("Content-Type", ContentType)
	w.Header().Set("Cache-Control", "no-store")
	// Disable proxy buffering so intermediaries forward frames immediately.
	w.Header().Set("X-Accel-Buffering", "no")
	w.WriteHeader(http.StatusOK)

	rc := http.NewResponseController(w)
	if err := rc.Flush(); err != nil && !errors.Is(err, http.ErrNotSupported) {
		h.Logger.Debug("http-stream: initial flush failed", "error", err)
		return
	}

	stream.LogStreamStart(h.Logger, Protocol, r.RemoteAddr, lease.ID, params)

	sink := &ndjsonSink{w: w, rc: rc, timeout: h.Limits.WriteTimeout}
	res := stream.Run(lease.Context(), params, protocol.EncoderFor(params.PayloadSize), sink, h.Counters, stream.Hooks{})
	h.Manager.Record(res)
	stream.LogStream(h.Logger, Protocol, r.RemoteAddr, lease.ID, params, res)
}

// ndjsonSink writes one frame per line and flushes after every frame.
type ndjsonSink struct {
	w       http.ResponseWriter
	rc      *http.ResponseController
	timeout time.Duration
}

func (s *ndjsonSink) WriteFrame(ctx context.Context, _ uint64, frame []byte) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if err := s.setWriteDeadline(); err != nil {
		return err
	}
	// Both writes are buffered by the ResponseWriter; a single flush per frame
	// keeps the frame atomic on the wire.
	if _, err := s.w.Write(frame); err != nil {
		return err
	}
	if _, err := s.w.Write(newline); err != nil {
		return err
	}
	return s.rc.Flush()
}

func (s *ndjsonSink) setWriteDeadline() error {
	err := s.rc.SetWriteDeadline(time.Now().Add(s.timeout))
	if errors.Is(err, http.ErrNotSupported) {
		return nil
	}
	return err
}
