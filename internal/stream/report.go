package stream

import (
	"log/slog"
	"time"
)

// LogStream writes the canonical per-stream lifecycle log entry. Per frame
// details are deliberately not logged: a long test emits hundreds of thousands
// of frames and would drown the log.
func LogStream(logger *slog.Logger, proto, remote string, id uint64, params Params, res Result) {
	attrs := []any{
		"stream_id", id,
		"protocol", proto,
		"remote", remote,
		"duration", params.Duration.String(),
		"interval", params.Interval.String(),
		"payload_size", params.PayloadSize,
		"frames", res.Frames,
		"bytes", res.BytesSent,
		"elapsed", res.Elapsed().Round(time.Millisecond).String(),
		"reason", string(res.Reason),
	}

	switch res.Reason {
	case EndCompleted:
		logger.Info("stream completed", attrs...)
	case EndWriteError:
		logger.Warn("stream error", append(attrs, "error", errText(res.Err))...)
	default:
		logger.Info("stream cancelled", append(attrs, "cause", errText(res.Err))...)
	}
}

// LogStreamStart writes the stream started entry.
func LogStreamStart(logger *slog.Logger, proto, remote string, id uint64, params Params) {
	logger.Info("stream started",
		"stream_id", id,
		"protocol", proto,
		"remote", remote,
		"duration", params.Duration.String(),
		"interval", params.Interval.String(),
		"payload_size", params.PayloadSize,
		"expected_frames", params.ExpectedFrames(),
	)
}

func errText(err error) string {
	if err == nil {
		return ""
	}
	return err.Error()
}
