// Package sse implements GET /api/stream/sse, a Server-Sent Events endpoint
// carrying the same StreamFrame payload as the other protocols.
package sse

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"strconv"
	"time"

	"github.com/nsst/streamtest/internal/httpx"
	"github.com/nsst/streamtest/internal/metrics"
	"github.com/nsst/streamtest/internal/stream"
	"github.com/nsst/streamtest/pkg/protocol"
)

// ContentType is the SSE media type.
const ContentType = "text/event-stream"

// Protocol is the identifier reported in logs and by /api/info.
const Protocol = "sse"

// EventName is the SSE event name used for every data frame.
const EventName = "data"

// heartbeatInterval is how long the stream may stay silent before a comment line
// is emitted. It keeps intermediaries from closing an idle connection and is also
// how a dead peer is noticed between widely spaced frames.
const heartbeatInterval = 15 * time.Second

var (
	openComment = []byte(": stream open\n\n")
	keepAlive   = []byte(": keep-alive\n\n")
)

// Handler serves the SSE endpoint.
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

	header := w.Header()
	header.Set("Content-Type", ContentType)
	header.Set("Cache-Control", "no-cache")
	header.Set("Connection", "keep-alive")
	// Disable proxy buffering so intermediaries forward events immediately.
	header.Set("X-Accel-Buffering", "no")
	w.WriteHeader(http.StatusOK)

	rc := http.NewResponseController(w)
	// Opening comment: commits the response and defeats any buffering proxy.
	if _, err := w.Write(openComment); err != nil {
		return
	}
	if err := rc.Flush(); err != nil && !errors.Is(err, http.ErrNotSupported) {
		h.Logger.Debug("sse: initial flush failed", "error", err)
		return
	}

	stream.LogStreamStart(h.Logger, Protocol, r.RemoteAddr, lease.ID, params)

	sink := &sseSink{w: w, rc: rc, timeout: h.Limits.WriteTimeout, last: time.Now()}
	res := stream.Run(lease.Context(), params, protocol.EncoderFor(params.PayloadSize), sink, h.Counters, stream.Hooks{})
	h.Manager.Record(res)
	stream.LogStream(h.Logger, Protocol, r.RemoteAddr, lease.ID, params, res)
}

// sseSink renders frames as SSE events:
//
//	id: 7
//	event: data
//	data: {"sequence":7,...}
type sseSink struct {
	w       http.ResponseWriter
	rc      *http.ResponseController
	timeout time.Duration
	last    time.Time
	buf     []byte
}

func (s *sseSink) WriteFrame(ctx context.Context, sequence uint64, frame []byte) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if err := s.setWriteDeadline(); err != nil {
		return err
	}

	if since := time.Since(s.last); since >= heartbeatInterval {
		if _, err := s.w.Write(keepAlive); err != nil {
			return err
		}
	}

	s.buf = append(s.buf[:0], "id: "...)
	s.buf = strconv.AppendUint(s.buf, sequence, 10)
	s.buf = append(s.buf, "\nevent: "...)
	s.buf = append(s.buf, EventName...)
	s.buf = append(s.buf, "\ndata: "...)
	s.buf = append(s.buf, frame...)
	s.buf = append(s.buf, "\n\n"...)

	if _, err := s.w.Write(s.buf); err != nil {
		return err
	}
	if err := s.rc.Flush(); err != nil {
		return fmt.Errorf("sse: flush: %w", err)
	}
	s.last = time.Now()
	return nil
}

func (s *sseSink) setWriteDeadline() error {
	err := s.rc.SetWriteDeadline(time.Now().Add(s.timeout))
	if errors.Is(err, http.ErrNotSupported) {
		return nil
	}
	return err
}
