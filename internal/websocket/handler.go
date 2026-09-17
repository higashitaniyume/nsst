// Package websocket implements GET /api/stream/ws, a WebSocket endpoint streaming
// StreamFrame JSON text messages.
package websocket

import (
	"context"
	"errors"
	"log/slog"
	"net/http"
	"time"

	ws "github.com/coder/websocket"

	"github.com/nsst/streamtest/internal/httpx"
	"github.com/nsst/streamtest/internal/metrics"
	"github.com/nsst/streamtest/internal/stream"
	"github.com/nsst/streamtest/pkg/protocol"
)

// Protocol is the identifier reported in logs and by /api/info.
const Protocol = "websocket"

// readLimit bounds what a peer may send us. The client is a pure consumer: it
// sends nothing, so anything beyond a tiny control message is a protocol
// violation worth rejecting early.
const readLimit = 1024

// closeGrace bounds the WebSocket closing handshake. A peer that never answers
// must not be able to delay an orderly shutdown.
const closeGrace = 2 * time.Second

// Handler serves the WebSocket endpoint.
type Handler struct {
	Limits   stream.Limits
	Manager  *stream.Manager
	Counters *metrics.Counters
	Logger   *slog.Logger
	CORS     httpx.CORS
}

// New builds a Handler.
func New(lim stream.Limits, mgr *stream.Manager, counters *metrics.Counters, logger *slog.Logger, cors httpx.CORS) *Handler {
	return &Handler{Limits: lim, Manager: mgr, Counters: counters, Logger: logger, CORS: cors}
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

	// Reserve a stream slot before the upgrade. The WebSocket endpoint must apply
	// the same concurrency and draining policy as the other protocols, and a
	// capacity error is far more useful to a client as an HTTP 503 than as a
	// close frame after a successful handshake.
	//
	// The parent context is deliberately context.Background(): net/http cancels
	// the request context as soon as the connection is hijacked for the upgrade,
	// so it cannot bound the lifetime of the stream. Peer disconnects are
	// observed through CloseRead below, with the per-write deadline as a backstop.
	lease, err := h.Manager.Acquire(context.Background())
	if err != nil {
		httpx.WriteAcquireError(w, err)
		return
	}
	defer lease.Release()

	opts := &ws.AcceptOptions{
		// Compression would distort throughput measurements and cost CPU.
		CompressionMode: ws.CompressionDisabled,
	}
	switch {
	case h.CORS.AllowAll():
		opts.InsecureSkipVerify = true
	case !h.CORS.Empty():
		opts.OriginPatterns = h.CORS.OriginPatterns()
	}

	c, err := ws.Accept(w, r, opts)
	if err != nil {
		// Accept has already written a response.
		h.Logger.Debug("websocket: upgrade failed", "error", err, "remote", r.RemoteAddr)
		return
	}
	defer c.CloseNow()
	c.SetReadLimit(readLimit)

	// CloseRead drives the read half of the connection: it answers control frames
	// and cancels the returned context as soon as the peer closes or misbehaves.
	ctx := c.CloseRead(lease.Context())

	stream.LogStreamStart(h.Logger, Protocol, r.RemoteAddr, lease.ID, params)

	sink := &messageSink{conn: c, timeout: h.Limits.WriteTimeout}
	res := stream.Run(ctx, params, protocol.EncoderFor(params.PayloadSize), sink, h.Counters, stream.Hooks{})
	h.Manager.Record(res)

	code, reason := closeCode(res.Reason)
	_ = closeWithGrace(c, code, reason, closeGrace)

	stream.LogStream(h.Logger, Protocol, r.RemoteAddr, lease.ID, params, res)
}

// closeWithGrace performs the closing handshake but never blocks longer than
// grace, after which the underlying connection is torn down.
func closeWithGrace(c *ws.Conn, code ws.StatusCode, reason string, grace time.Duration) error {
	done := make(chan error, 1)
	go func() { done <- c.Close(code, reason) }()

	timer := time.NewTimer(grace)
	defer timer.Stop()

	select {
	case err := <-done:
		return err
	case <-timer.C:
		return c.CloseNow()
	}
}

func closeCode(reason stream.EndReason) (ws.StatusCode, string) {
	switch reason {
	case stream.EndCompleted:
		return ws.StatusNormalClosure, "stream completed"
	case stream.EndShutdown:
		return ws.StatusGoingAway, "server shutting down"
	case stream.EndWriteError:
		return ws.StatusInternalError, "stream write failure"
	default:
		return ws.StatusNormalClosure, "client closed"
	}
}

// messageSink writes one JSON text message per frame.
type messageSink struct {
	conn    *ws.Conn
	timeout time.Duration
}

func (s *messageSink) WriteFrame(ctx context.Context, _ uint64, frame []byte) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	// A cancelled write context makes coder/websocket tear the connection down,
	// which is exactly the back pressure behaviour we want for a stalled reader.
	writeCtx, cancel := context.WithTimeout(ctx, s.timeout)
	defer cancel()
	return s.conn.Write(writeCtx, ws.MessageText, frame)
}
