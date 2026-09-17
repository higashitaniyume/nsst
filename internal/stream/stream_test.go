package stream

import (
	"context"
	"errors"
	"sync"
	"testing"
	"time"

	"github.com/nsst/streamtest/internal/metrics"
	"github.com/nsst/streamtest/pkg/protocol"
)

// recordingSink captures the frames handed to it.
type recordingSink struct {
	mu      sync.Mutex
	frames  []protocol.Frame
	bytes   int
	failOn  uint64
	failErr error
}

func (s *recordingSink) WriteFrame(_ context.Context, sequence uint64, frame []byte) error {
	if s.failOn != 0 && sequence == s.failOn {
		return s.failErr
	}
	f, err := protocol.Decode(frame)
	if err != nil {
		return err
	}
	if err := f.Validate(); err != nil {
		return err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.frames = append(s.frames, f)
	s.bytes += len(frame)
	return nil
}

func (s *recordingSink) snapshot() ([]protocol.Frame, int) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return append([]protocol.Frame(nil), s.frames...), s.bytes
}

func TestRunEmitsExpectedFrames(t *testing.T) {
	params := Params{Duration: time.Second, Interval: 100 * time.Millisecond, PayloadSize: 64}
	sink := &recordingSink{}

	res := Run(context.Background(), params, protocol.EncoderFor(params.PayloadSize), sink, nil, Hooks{})

	if !res.Completed() {
		t.Fatalf("reason = %s, err = %v", res.Reason, res.Err)
	}
	frames, bytes := sink.snapshot()
	if want := int(params.ExpectedFrames()); len(frames) != want {
		t.Fatalf("emitted %d frames, want %d", len(frames), want)
	}
	if res.Frames != uint64(len(frames)) {
		t.Fatalf("result frames = %d, sink saw %d", res.Frames, len(frames))
	}
	if res.BytesSent != uint64(bytes) {
		t.Fatalf("result bytes = %d, sink saw %d", res.BytesSent, bytes)
	}

	for i, f := range frames {
		if f.Sequence != uint64(i+1) {
			t.Fatalf("frame %d has sequence %d; sequences must start at 1 and be contiguous", i, f.Sequence)
		}
		if f.PayloadSize != params.PayloadSize || len(f.Payload) != params.PayloadSize {
			t.Fatalf("frame %d payload = %d/%d bytes, want %d", i, f.PayloadSize, len(f.Payload), params.PayloadSize)
		}
	}

	// server_time must be monotonically non-decreasing.
	for i := 1; i < len(frames); i++ {
		if frames[i].ServerTime < frames[i-1].ServerTime {
			t.Fatalf("server_time went backwards at frame %d", i)
		}
	}
}

func TestRunFirstFrameIsImmediate(t *testing.T) {
	params := Params{Duration: 3 * time.Second, Interval: 500 * time.Millisecond}
	sink := &recordingSink{}

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	stop := make(chan struct{})
	defer close(stop)
	go func() {
		ticker := time.NewTicker(2 * time.Millisecond)
		defer ticker.Stop()
		for {
			select {
			case <-stop:
				return
			case <-ticker.C:
				if frames, _ := sink.snapshot(); len(frames) > 0 {
					cancel()
					return
				}
			}
		}
	}()

	start := time.Now()
	res := Run(ctx, params, protocol.NewEncoder(0), sink, nil, Hooks{})
	elapsed := time.Since(start)

	if res.Reason != EndClientClosed {
		t.Fatalf("reason = %s, want %s", res.Reason, EndClientClosed)
	}
	if elapsed > time.Second {
		t.Fatalf("first frame took %v; it must be written without waiting for the first interval", elapsed)
	}
}

func TestRunReportsWriteErrors(t *testing.T) {
	params := Params{Duration: time.Second, Interval: 10 * time.Millisecond}
	writeErr := errors.New("connection reset")
	sink := &recordingSink{failOn: 3, failErr: writeErr}

	res := Run(context.Background(), params, protocol.NewEncoder(0), sink, nil, Hooks{})

	if res.Reason != EndWriteError {
		t.Fatalf("reason = %s, want %s", res.Reason, EndWriteError)
	}
	if !errors.Is(res.Err, writeErr) {
		t.Fatalf("err = %v, want %v", res.Err, writeErr)
	}
	frames, _ := sink.snapshot()
	if len(frames) != 2 {
		t.Fatalf("sink accepted %d frames, want 2 before the failure", len(frames))
	}
	if res.Frames != 2 {
		t.Fatalf("result frames = %d, want 2", res.Frames)
	}
}

func TestRunReportsShutdown(t *testing.T) {
	params := Params{Duration: time.Hour, Interval: 10 * time.Millisecond}
	sink := &recordingSink{}

	mgr := NewManager(DefaultLimits(), nil)
	lease, err := mgr.Acquire(context.Background())
	if err != nil {
		t.Fatalf("Acquire: %v", err)
	}
	defer lease.Release()

	done := make(chan Result, 1)
	go func() { done <- Run(lease.Context(), params, protocol.NewEncoder(0), sink, nil, Hooks{}) }()

	WaitForFrames(t, sink, 3)
	mgr.ForceClose()

	select {
	case res := <-done:
		if res.Reason != EndShutdown {
			t.Fatalf("reason = %s, want %s", res.Reason, EndShutdown)
		}
		if !errors.Is(res.Err, ErrServerShutdown) {
			t.Fatalf("err = %v, want ErrServerShutdown", res.Err)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("Run did not return after ForceClose")
	}
}

func TestRunInvokesHooksAndCounters(t *testing.T) {
	params := Params{Duration: 300 * time.Millisecond, Interval: 100 * time.Millisecond, PayloadSize: 8}
	sink := &recordingSink{}
	counters := metrics.New()

	var hooked uint64
	res := Run(context.Background(), params, protocol.EncoderFor(8), sink, counters,
		Hooks{OnFrame: func(seq uint64, wireBytes int) {
			hooked++
			if wireBytes <= 0 {
				t.Errorf("hook reported %d wire bytes for frame %d", wireBytes, seq)
			}
		}})

	if hooked != res.Frames {
		t.Fatalf("hook fired %d times, result frames %d", hooked, res.Frames)
	}
	if got := counters.FramesSent.Load(); got != res.Frames {
		t.Fatalf("frames_sent = %d, want %d", got, res.Frames)
	}
	if got := counters.BytesSent.Load(); got != res.BytesSent {
		t.Fatalf("bytes_sent = %d, want %d", got, res.BytesSent)
	}
}

// WaitForFrames blocks until the sink has received at least n frames.
func WaitForFrames(t *testing.T, sink *recordingSink, n int) {
	t.Helper()
	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		if frames, _ := sink.snapshot(); len(frames) >= n {
			return
		}
		time.Sleep(5 * time.Millisecond)
	}
	t.Fatalf("timed out waiting for %d frames", n)
}
