package websocket_test

import (
	"context"
	"net/http"
	"runtime"
	"testing"
	"time"

	ws "github.com/coder/websocket"

	"github.com/nsst/streamtest/internal/testsupport"
	"github.com/nsst/streamtest/pkg/protocol"
)

// runSession dials the endpoint, drains the messages the server sends and
// returns the decoded frames plus the close status the peer reported.
func runSession(t *testing.T, stack *testsupport.Stack, query string, timeout time.Duration) ([]protocol.Frame, ws.StatusCode) {
	t.Helper()

	ctx, cancel := context.WithTimeout(context.Background(), timeout)
	defer cancel()

	conn, resp, err := ws.Dial(ctx, stack.WSURL("/api/stream/ws?"+query), nil)
	if err != nil {
		status := 0
		if resp != nil {
			status = resp.StatusCode
		}
		t.Fatalf("Dial: %v (http status %d)", err, status)
	}
	defer conn.CloseNow()

	var frames []protocol.Frame
	for {
		typ, data, err := conn.Read(ctx)
		if err != nil {
			return frames, ws.CloseStatus(err)
		}
		if typ != ws.MessageText {
			t.Fatalf("message type = %v, want text", typ)
		}
		frame, err := protocol.Decode(data)
		if err != nil {
			t.Fatalf("decode frame: %v", err)
		}
		if err := frame.Validate(); err != nil {
			t.Fatalf("invalid frame: %v", err)
		}
		frames = append(frames, frame)
	}
}

func TestWebSocketHandshake(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()

	conn, resp, err := ws.Dial(ctx, stack.WSURL("/api/stream/ws?duration=1&interval=500&payload_size=0"), nil)
	if err != nil {
		t.Fatalf("Dial: %v", err)
	}
	defer conn.CloseNow()

	if resp.StatusCode != http.StatusSwitchingProtocols {
		t.Fatalf("http status = %d, want 101", resp.StatusCode)
	}
	// Compression must be off so that throughput measurements are meaningful.
	if ext := resp.Header.Get("Sec-WebSocket-Extensions"); ext != "" {
		t.Fatalf("server negotiated an extension: %q", ext)
	}
}

func TestWebSocketStreamsFramesInOrder(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	frames, code := runSession(t, stack, "duration=1&interval=100&payload_size=128", 15*time.Second)

	if code != ws.StatusNormalClosure {
		t.Fatalf("close status = %v, want %v", code, ws.StatusNormalClosure)
	}
	if want := 10; len(frames) != want {
		t.Fatalf("received %d frames, want %d", len(frames), want)
	}
	for i, f := range frames {
		if f.Sequence != uint64(i+1) {
			t.Fatalf("frame %d sequence = %d, want %d", i, f.Sequence, i+1)
		}
		if f.PayloadSize != 128 || len(f.Payload) != 128 {
			t.Fatalf("frame %d payload = %d/%d bytes, want 128", i, f.PayloadSize, len(f.Payload))
		}
		if f.ServerTime <= 0 {
			t.Fatalf("frame %d server_time = %d", i, f.ServerTime)
		}
	}

	if got := stack.Counters.StreamsFinished.Load(); got != 1 {
		t.Fatalf("streams_finished = %d, want 1", got)
	}

	// The lease is released when the handler returns, which happens shortly after
	// the peer observes the close frame. Require it to be prompt: a slow path here
	// would mean the closing handshake is waiting on its fallback grace period.
	start := time.Now()
	testsupport.WaitFor(t, "stream slot released", 5*time.Second, func() bool {
		return stack.Manager.Active() == 0
	})
	if elapsed := time.Since(start); elapsed > time.Second {
		t.Fatalf("server held the stream slot for %v after the client saw the close frame", elapsed)
	}
}

func TestWebSocketRespectsDuration(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	start := time.Now()
	frames, code := runSession(t, stack, "duration=2&interval=500&payload_size=0", 15*time.Second)
	elapsed := time.Since(start)

	if code != ws.StatusNormalClosure {
		t.Fatalf("close status = %v, want normal closure", code)
	}
	if len(frames) != 4 {
		t.Fatalf("received %d frames, want 4", len(frames))
	}
	if elapsed < 1800*time.Millisecond {
		t.Fatalf("session lasted %v, shorter than the requested 2s", elapsed)
	}
}

func TestWebSocketClientCloseStopsStream(t *testing.T) {
	lim := testsupport.FastLimits()
	lim.MaxDuration = time.Minute
	stack := testsupport.New(lim, nil)
	defer stack.Close()

	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()

	conn, _, err := ws.Dial(ctx, stack.WSURL("/api/stream/ws?duration=30&interval=50&payload_size=16"), nil)
	if err != nil {
		t.Fatalf("Dial: %v", err)
	}

	for i := 0; i < 2; i++ {
		if _, _, err := conn.Read(ctx); err != nil {
			t.Fatalf("read frame %d: %v", i, err)
		}
	}
	testsupport.WaitFor(t, "one active stream", 3*time.Second, func() bool {
		return stack.Manager.Active() == 1
	})

	// Client initiated close: the server must observe it and release the slot.
	if err := conn.Close(ws.StatusNormalClosure, "client done"); err != nil {
		t.Fatalf("client Close: %v", err)
	}

	testsupport.WaitFor(t, "stream teardown", 5*time.Second, func() bool {
		return stack.Manager.Active() == 0
	})
	if got := stack.Counters.CancelledStreams.Load(); got == 0 {
		t.Fatalf("cancelled_streams = %d, want at least 1", got)
	}
}

func TestWebSocketAbruptDisconnectStopsStream(t *testing.T) {
	lim := testsupport.FastLimits()
	lim.MaxDuration = time.Minute
	stack := testsupport.New(lim, nil)
	defer stack.Close()

	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()

	conn, _, err := ws.Dial(ctx, stack.WSURL("/api/stream/ws?duration=30&interval=50&payload_size=16"), nil)
	if err != nil {
		t.Fatalf("Dial: %v", err)
	}
	if _, _, err := conn.Read(ctx); err != nil {
		t.Fatalf("read frame: %v", err)
	}
	testsupport.WaitFor(t, "one active stream", 3*time.Second, func() bool {
		return stack.Manager.Active() == 1
	})

	// No close handshake at all: drop the TCP connection.
	_ = conn.CloseNow()

	testsupport.WaitFor(t, "stream teardown", 5*time.Second, func() bool {
		return stack.Manager.Active() == 0
	})
}

func TestWebSocketRejectsInvalidParams(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	conn, resp, err := ws.Dial(ctx, stack.WSURL("/api/stream/ws?duration=99999"), nil)
	if err == nil {
		_ = conn.CloseNow()
		t.Fatal("expected the handshake to fail for an out of range duration")
	}
	if resp == nil {
		t.Fatalf("no HTTP response: %v", err)
	}
	if resp.StatusCode != http.StatusBadRequest {
		t.Fatalf("status = %d, want 400", resp.StatusCode)
	}
	_ = resp.Body.Close()
}

func TestWebSocketDoesNotLeakGoroutines(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	// Warm up so that connection pools and one-off setup goroutines do not count.
	if _, code := runSession(t, stack, "duration=1&interval=100&payload_size=0", 15*time.Second); code != ws.StatusNormalClosure {
		t.Fatalf("warm up close status = %v", code)
	}
	testsupport.WaitFor(t, "warm up teardown", 5*time.Second, func() bool { return stack.Manager.Active() == 0 })
	time.Sleep(100 * time.Millisecond)
	runtime.GC()
	before := runtime.NumGoroutine()

	const sessions = 8
	for i := 0; i < sessions; i++ {
		if _, code := runSession(t, stack, "duration=1&interval=100&payload_size=0", 15*time.Second); code != ws.StatusNormalClosure {
			t.Fatalf("session %d close status = %v", i, code)
		}
	}

	testsupport.WaitFor(t, "stream slots released", 10*time.Second, func() bool {
		return stack.Manager.Active() == 0
	})
	testsupport.WaitFor(t, "goroutines to settle", 10*time.Second, func() bool {
		runtime.GC()
		return runtime.NumGoroutine() <= before+4
	})
}
