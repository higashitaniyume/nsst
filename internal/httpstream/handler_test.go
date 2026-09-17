package httpstream_test

import (
	"bufio"
	"context"
	"encoding/json"
	"io"
	"net/http"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/nsst/streamtest/internal/testsupport"
	"github.com/nsst/streamtest/pkg/protocol"
)

// getFrames issues a request and decodes every NDJSON frame from the response.
func getFrames(t *testing.T, stack *testsupport.Stack, query string) ([]protocol.Frame, *http.Response) {
	t.Helper()

	resp, err := http.Get(stack.URL("/api/stream/http?" + query))
	if err != nil {
		t.Fatalf("GET: %v", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		body, _ := io.ReadAll(io.LimitReader(resp.Body, 4096))
		t.Fatalf("status = %d, body = %s", resp.StatusCode, body)
	}

	var frames []protocol.Frame
	scanner := bufio.NewScanner(resp.Body)
	scanner.Buffer(make([]byte, 0, 64*1024), 8<<20)
	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "" {
			continue
		}
		if !json.Valid([]byte(line)) {
			t.Fatalf("frame is not valid JSON: %q", line)
		}
		frame, err := protocol.Decode([]byte(line))
		if err != nil {
			t.Fatalf("decode %q: %v", line, err)
		}
		if err := frame.Validate(); err != nil {
			t.Fatalf("invalid frame %q: %v", line, err)
		}
		frames = append(frames, frame)
	}
	if err := scanner.Err(); err != nil {
		t.Fatalf("read stream: %v", err)
	}
	return frames, resp
}

func TestHTTPStreamContentType(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	resp, err := http.Get(stack.URL("/api/stream/http?duration=1&interval=500&payload_size=0"))
	if err != nil {
		t.Fatalf("GET: %v", err)
	}
	defer resp.Body.Close()

	if got := resp.Header.Get("Content-Type"); got != "application/x-ndjson" {
		t.Errorf("Content-Type = %q, want application/x-ndjson", got)
	}
	if got := resp.Header.Get("Cache-Control"); got != "no-store" {
		t.Errorf("Cache-Control = %q, want no-store", got)
	}
	_, _ = io.Copy(io.Discard, resp.Body)
}

func TestHTTPStreamFrameSequenceAndShape(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	frames, _ := getFrames(t, stack, "duration=1&interval=100&payload_size=64")

	if want := 10; len(frames) != want {
		t.Fatalf("received %d frames, want %d", len(frames), want)
	}
	for i, f := range frames {
		if f.Sequence != uint64(i+1) {
			t.Fatalf("frame %d sequence = %d, want %d", i, f.Sequence, i+1)
		}
		if f.ServerTime <= 0 {
			t.Errorf("frame %d server_time = %d", i, f.ServerTime)
		}
		if f.PayloadSize != 64 {
			t.Errorf("frame %d payload_size = %d, want 64", i, f.PayloadSize)
		}
		if len(f.Payload) != 64 {
			t.Errorf("frame %d carried %d payload bytes, want 64", i, len(f.Payload))
		}
	}
}

func TestHTTPStreamRespectsPayloadSize(t *testing.T) {
	for _, size := range []int{0, 1, 4096} {
		t.Run("payload_"+strconv.Itoa(size), func(t *testing.T) {
			stack := testsupport.New(testsupport.FastLimits(), nil)
			defer stack.Close()

			frames, _ := getFrames(t, stack, "duration=1&interval=500&payload_size="+strconv.Itoa(size))
			if len(frames) != 2 {
				t.Fatalf("received %d frames, want 2", len(frames))
			}
			for _, f := range frames {
				if f.PayloadSize != size {
					t.Fatalf("payload_size = %d, want %d", f.PayloadSize, size)
				}
				if len(f.Payload) != size {
					t.Fatalf("payload bytes = %d, want %d", len(f.Payload), size)
				}
			}
		})
	}
}

func TestHTTPStreamRespectsInterval(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	start := time.Now()
	frames, _ := getFrames(t, stack, "duration=1&interval=250&payload_size=0")
	elapsed := time.Since(start)

	if len(frames) != 4 {
		t.Fatalf("received %d frames, want 4", len(frames))
	}
	// Four frames on a 250ms grid over one second: the loop stops at t=1s.
	if elapsed < 900*time.Millisecond {
		t.Errorf("stream finished in %v, faster than the requested duration", elapsed)
	}
	if elapsed > 4*time.Second {
		t.Errorf("stream took %v, far longer than the requested duration", elapsed)
	}
}

func TestHTTPStreamRespectsDuration(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	start := time.Now()
	frames, _ := getFrames(t, stack, "duration=2&interval=500&payload_size=0")
	elapsed := time.Since(start)

	if len(frames) != 4 {
		t.Fatalf("received %d frames, want 4", len(frames))
	}
	if elapsed < 1800*time.Millisecond {
		t.Errorf("stream ended after %v, before the requested 2s", elapsed)
	}
}

func TestHTTPStreamFlushesFirstFrameImmediately(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	// The stream lasts five seconds; the first frame must not wait for the end.
	start := time.Now()
	resp, err := http.Get(stack.URL("/api/stream/http?duration=5&interval=500&payload_size=16"))
	if err != nil {
		t.Fatalf("GET: %v", err)
	}
	defer resp.Body.Close()

	line, err := bufio.NewReader(resp.Body).ReadString('\n')
	if err != nil {
		t.Fatalf("read first frame: %v", err)
	}
	if elapsed := time.Since(start); elapsed > 2*time.Second {
		t.Fatalf("first frame took %v; the response was buffered instead of flushed", elapsed)
	}

	frame, err := protocol.Decode([]byte(strings.TrimSpace(line)))
	if err != nil {
		t.Fatalf("decode first frame: %v", err)
	}
	if frame.Sequence != 1 {
		t.Fatalf("first frame sequence = %d, want 1", frame.Sequence)
	}
}

func TestHTTPStreamStopsOnClientDisconnect(t *testing.T) {
	lim := testsupport.FastLimits()
	lim.MaxDuration = time.Minute
	stack := testsupport.New(lim, nil)
	defer stack.Close()

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	req, err := http.NewRequestWithContext(ctx, http.MethodGet,
		stack.URL("/api/stream/http?duration=30&interval=50&payload_size=32"), nil)
	if err != nil {
		t.Fatalf("NewRequest: %v", err)
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatalf("Do: %v", err)
	}

	if _, err := bufio.NewReader(resp.Body).ReadString('\n'); err != nil {
		t.Fatalf("read first frame: %v", err)
	}
	testsupport.WaitFor(t, "one active stream", 3*time.Second, func() bool {
		return stack.Manager.Active() == 1
	})

	// Abort the request; the server must notice and release the stream.
	cancel()
	_ = resp.Body.Close()

	testsupport.WaitFor(t, "stream teardown", 5*time.Second, func() bool {
		return stack.Manager.Active() == 0
	})
	if got := stack.Counters.CancelledStreams.Load(); got == 0 {
		t.Fatalf("cancelled_streams = %d, want at least 1", got)
	}
}
