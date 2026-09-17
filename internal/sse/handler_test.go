package sse_test

import (
	"bufio"
	"context"
	"io"
	"net/http"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/nsst/streamtest/internal/testsupport"
	"github.com/nsst/streamtest/pkg/protocol"
)

// event is one parsed SSE message.
type event struct {
	id    string
	name  string
	data  string
	other []string
}

// readEvents parses an SSE response until the body ends.
func readEvents(t *testing.T, body io.Reader) []event {
	t.Helper()

	var (
		events  []event
		current event
		seen    bool
	)
	scanner := bufio.NewScanner(body)
	scanner.Buffer(make([]byte, 0, 64*1024), 8<<20)

	for scanner.Scan() {
		line := scanner.Text()
		switch {
		case line == "":
			if seen {
				events = append(events, current)
				current = event{}
				seen = false
			}
		case strings.HasPrefix(line, ":"):
			// Comment / heartbeat.
		case strings.HasPrefix(line, "id:"):
			current.id = strings.TrimSpace(strings.TrimPrefix(line, "id:"))
			seen = true
		case strings.HasPrefix(line, "event:"):
			current.name = strings.TrimSpace(strings.TrimPrefix(line, "event:"))
			seen = true
		case strings.HasPrefix(line, "data:"):
			current.data = strings.TrimSpace(strings.TrimPrefix(line, "data:"))
			seen = true
		default:
			current.other = append(current.other, line)
			seen = true
		}
	}
	if err := scanner.Err(); err != nil {
		t.Fatalf("read SSE body: %v", err)
	}
	return events
}

func getEvents(t *testing.T, stack *testsupport.Stack, query string) ([]event, *http.Response) {
	t.Helper()

	resp, err := http.Get(stack.URL("/api/stream/sse?" + query))
	if err != nil {
		t.Fatalf("GET: %v", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		body, _ := io.ReadAll(io.LimitReader(resp.Body, 4096))
		t.Fatalf("status = %d, body = %s", resp.StatusCode, body)
	}
	return readEvents(t, resp.Body), resp
}

func TestSSEHeaders(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	_, resp := getEvents(t, stack, "duration=1&interval=500&payload_size=0")

	if got := resp.Header.Get("Content-Type"); got != "text/event-stream" {
		t.Errorf("Content-Type = %q, want text/event-stream", got)
	}
	if got := resp.Header.Get("Cache-Control"); got != "no-cache" {
		t.Errorf("Cache-Control = %q, want no-cache", got)
	}
	if got := resp.Header.Get("Connection"); got != "keep-alive" {
		t.Errorf("Connection = %q, want keep-alive", got)
	}
}

func TestSSEEventFormat(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	events, _ := getEvents(t, stack, "duration=1&interval=100&payload_size=64")

	if want := 10; len(events) != want {
		t.Fatalf("received %d events, want %d", len(events), want)
	}

	for i, ev := range events {
		if ev.name != "data" {
			t.Errorf("event %d name = %q, want %q", i, ev.name, "data")
		}
		if ev.id != strconv.Itoa(i+1) {
			t.Errorf("event %d id = %q, want %d", i, ev.id, i+1)
		}
		if len(ev.other) != 0 {
			t.Errorf("event %d has unexpected fields: %v", i, ev.other)
		}

		frame, err := protocol.Decode([]byte(ev.data))
		if err != nil {
			t.Fatalf("event %d data does not decode: %v (%q)", i, err, ev.data)
		}
		if err := frame.Validate(); err != nil {
			t.Fatalf("event %d invalid frame: %v", i, err)
		}
		if frame.Sequence != uint64(i+1) {
			t.Errorf("event %d sequence = %d, want %d", i, frame.Sequence, i+1)
		}
		if frame.PayloadSize != 64 || len(frame.Payload) != 64 {
			t.Errorf("event %d payload = %d/%d bytes, want 64", i, frame.PayloadSize, len(frame.Payload))
		}
		if frame.ServerTime <= 0 {
			t.Errorf("event %d server_time = %d", i, frame.ServerTime)
		}
	}
}

func TestSSEStartsWithCommentAndFlushesEarly(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	start := time.Now()
	resp, err := http.Get(stack.URL("/api/stream/sse?duration=5&interval=500&payload_size=16"))
	if err != nil {
		t.Fatalf("GET: %v", err)
	}
	defer resp.Body.Close()

	line, err := bufio.NewReader(resp.Body).ReadString('\n')
	if err != nil {
		t.Fatalf("read opening line: %v", err)
	}
	if elapsed := time.Since(start); elapsed > 2*time.Second {
		t.Fatalf("opening comment took %v; the response was buffered", elapsed)
	}
	if !strings.HasPrefix(strings.TrimSpace(line), ":") {
		t.Fatalf("first line = %q, want an SSE comment", line)
	}
}

func TestSSEStopsOnClientDisconnect(t *testing.T) {
	lim := testsupport.FastLimits()
	lim.MaxDuration = time.Minute
	stack := testsupport.New(lim, nil)
	defer stack.Close()

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	req, err := http.NewRequestWithContext(ctx, http.MethodGet,
		stack.URL("/api/stream/sse?duration=30&interval=50&payload_size=32"), nil)
	if err != nil {
		t.Fatalf("NewRequest: %v", err)
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatalf("Do: %v", err)
	}

	reader := bufio.NewReader(resp.Body)
	// Opening comment, then the first data line.
	if _, err := reader.ReadString('\n'); err != nil {
		t.Fatalf("read opening comment: %v", err)
	}
	testsupport.WaitFor(t, "one active stream", 3*time.Second, func() bool {
		return stack.Manager.Active() == 1
	})

	cancel()
	_ = resp.Body.Close()

	testsupport.WaitFor(t, "stream teardown", 5*time.Second, func() bool {
		return stack.Manager.Active() == 0
	})
	if got := stack.Counters.CancelledStreams.Load(); got == 0 {
		t.Fatalf("cancelled_streams = %d, want at least 1", got)
	}
}

func TestSSECompletesNaturally(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	events, _ := getEvents(t, stack, "duration=1&interval=250&payload_size=0")
	if len(events) != 4 {
		t.Fatalf("received %d events, want 4", len(events))
	}
	if got := stack.Counters.StreamsFinished.Load(); got != 1 {
		t.Fatalf("streams_finished = %d, want 1", got)
	}
	if got := stack.Manager.Active(); got != 0 {
		t.Fatalf("active streams = %d, want 0", got)
	}
}
