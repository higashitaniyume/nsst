package stream

import (
	"context"
	"errors"
	"time"

	"github.com/nsst/streamtest/internal/metrics"
	"github.com/nsst/streamtest/pkg/protocol"
)

// EndReason describes why an emit loop stopped.
type EndReason string

const (
	// EndCompleted means the requested duration elapsed and every scheduled frame
	// was written.
	EndCompleted EndReason = "completed"
	// EndClientClosed means the peer went away (request context cancelled, TCP
	// reset, WebSocket close frame, ...).
	EndClientClosed EndReason = "client_closed"
	// EndWriteError means a write failed while the connection was still expected
	// to be alive, e.g. the per-frame write deadline expired because the client
	// stopped reading.
	EndWriteError EndReason = "write_error"
	// EndShutdown means the server began shutting down and the stream was cut
	// short by the drain deadline.
	EndShutdown EndReason = "server_shutdown"
)

// Sink transports an encoded frame over a concrete protocol. Implementations own
// their own framing (newline, SSE fields, WebSocket message) and must unblock as
// soon as ctx is cancelled.
//
// sequence is passed alongside the encoded frame because some protocols need it
// outside of the JSON body, e.g. the SSE "id:" field.
type Sink interface {
	WriteFrame(ctx context.Context, sequence uint64, frame []byte) error
}

// Hooks receives per frame notifications. All fields are optional.
type Hooks struct {
	// OnFrame is called after a frame has been written successfully.
	OnFrame func(sequence uint64, wireBytes int)
}

// Result summarises one completed emit loop.
type Result struct {
	StartedAt time.Time
	EndedAt   time.Time
	Frames    uint64
	BytesSent uint64
	Reason    EndReason
	Err       error
}

// Elapsed is the wall clock duration of the emit loop.
func (r Result) Elapsed() time.Duration { return r.EndedAt.Sub(r.StartedAt) }

// Completed reports whether the loop ran to its natural end.
func (r Result) Completed() bool { return r.Reason == EndCompleted }

// Run emits frames for params until the duration elapses, the peer disappears,
// the server shuts down or a write fails.
//
// The first frame is written immediately (t=0) so that the client can measure
// first frame latency, then frames follow on a fixed interval grid.
func Run(ctx context.Context, params Params, enc *protocol.Encoder, sink Sink, counters *metrics.Counters, hooks Hooks) Result {
	res := Result{StartedAt: time.Now(), Reason: EndCompleted}
	deadline := res.StartedAt.Add(params.Duration)

	pacer := NewPacer(params.Interval)
	defer pacer.Stop()

	buf := make([]byte, 0, 256+enc.PayloadSize())
	var seq uint64
	// offset walks the payload document one chunk per frame; the encoder wraps it
	// at the end of the document so that a long test never runs out of text.
	var offset int

	for {
		emit, err := pacer.Wait(ctx, deadline)
		if err != nil {
			res.EndedAt = time.Now()
			res.Reason, res.Err = classify(context.Cause(ctx))
			return res
		}
		if !emit {
			res.EndedAt = time.Now()
			res.Reason = EndCompleted
			return res
		}

		now := time.Now()
		seq++
		var consumed int
		buf, consumed = enc.Append(buf[:0], seq, now.UnixMilli(), offset)
		offset += consumed

		if err := sink.WriteFrame(ctx, seq, buf); err != nil {
			res.EndedAt = time.Now()
			if ctx.Err() != nil {
				res.Reason, res.Err = classify(context.Cause(ctx))
			} else {
				res.Reason, res.Err = EndWriteError, err
			}
			return res
		}

		res.Frames = seq
		res.BytesSent += uint64(len(buf))
		if counters != nil {
			counters.FramesSent.Add(1)
			counters.BytesSent.Add(uint64(len(buf)))
		}
		if hooks.OnFrame != nil {
			hooks.OnFrame(seq, len(buf))
		}
	}
}

func classify(cause error) (EndReason, error) {
	switch {
	case cause == nil:
		return EndCompleted, nil
	case errors.Is(cause, ErrServerShutdown):
		return EndShutdown, cause
	case errors.Is(cause, context.DeadlineExceeded):
		return EndWriteError, cause
	default:
		return EndClientClosed, cause
	}
}
