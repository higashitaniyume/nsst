package api_test

import (
	"bufio"
	"context"
	"encoding/json"
	"io"
	"net/http"
	"strings"
	"testing"
	"time"

	"github.com/nsst/streamtest/internal/api"
	"github.com/nsst/streamtest/internal/httpx"
	"github.com/nsst/streamtest/internal/testsupport"
)

// errorBody mirrors httpx.ErrorBody for assertions.
type errorBody struct {
	Error   string `json:"error"`
	Message string `json:"message"`
	Param   string `json:"param"`
}

func getJSON(t *testing.T, stack *testsupport.Stack, path string, out any) *http.Response {
	t.Helper()

	resp, err := http.Get(stack.URL(path))
	if err != nil {
		t.Fatalf("GET %s: %v", path, err)
	}
	defer resp.Body.Close()

	body, err := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if err != nil {
		t.Fatalf("read %s: %v", path, err)
	}
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("GET %s status = %d, body = %s", path, resp.StatusCode, body)
	}
	if out != nil {
		if err := json.Unmarshal(body, out); err != nil {
			t.Fatalf("decode %s: %v (%s)", path, err, body)
		}
	}
	return resp
}

func TestHealth(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	var got struct {
		Status     string `json:"status"`
		Version    string `json:"version"`
		UptimeSecs int64  `json:"uptime_seconds"`
	}
	resp := getJSON(t, stack, "/api/health", &got)

	if got.Status != "ok" {
		t.Errorf("status = %q, want ok", got.Status)
	}
	if got.Version == "" {
		t.Error("version must not be empty")
	}
	if ct := resp.Header.Get("Content-Type"); !strings.HasPrefix(ct, "application/json") {
		t.Errorf("Content-Type = %q, want application/json", ct)
	}
}

func TestInfo(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	var got struct {
		Name      string            `json:"name"`
		Version   string            `json:"version"`
		Protocols []string          `json:"protocols"`
		Endpoints map[string]string `json:"endpoints"`
		Limits    struct {
			MaxDuration    int64 `json:"max_duration"`
			MinInterval    int64 `json:"min_interval"`
			MaxPayloadSize int   `json:"max_payload_size"`
		} `json:"limits"`
	}
	getJSON(t, stack, "/api/info", &got)

	if got.Name != "StreamTest" {
		t.Errorf("name = %q, want StreamTest", got.Name)
	}
	if got.Version == "" {
		t.Error("version must not be empty")
	}
	want := []string{"http-stream", "sse", "websocket"}
	if len(got.Protocols) != len(want) {
		t.Fatalf("protocols = %v, want %v", got.Protocols, want)
	}
	for i := range want {
		if got.Protocols[i] != want[i] {
			t.Fatalf("protocols = %v, want %v", got.Protocols, want)
		}
	}
	if got.Endpoints["http"] == "" || got.Endpoints["sse"] == "" || got.Endpoints["websocket"] == "" {
		t.Errorf("endpoints incomplete: %v", got.Endpoints)
	}
	if got.Limits.MaxDuration != 5 || got.Limits.MinInterval != 10 || got.Limits.MaxPayloadSize != 64<<10 {
		t.Errorf("limits = %+v", got.Limits)
	}
}

func TestConfig(t *testing.T) {
	lim := testsupport.FastLimits()
	stack := testsupport.New(lim, nil)
	defer stack.Close()

	var got struct {
		Name      string   `json:"name"`
		Version   string   `json:"version"`
		Protocols []string `json:"protocols"`
		Limits    struct {
			MaxDuration          int64 `json:"max_duration"`
			MinDuration          int64 `json:"min_duration"`
			MinInterval          int64 `json:"min_interval"`
			MaxInterval          int64 `json:"max_interval"`
			MaxPayloadSize       int   `json:"max_payload_size"`
			MaxConcurrentStreams int   `json:"max_concurrent_streams"`
		} `json:"limits"`
		Defaults struct {
			Duration    int64 `json:"duration"`
			Interval    int64 `json:"interval"`
			PayloadSize int   `json:"payload_size"`
		} `json:"defaults"`
	}
	getJSON(t, stack, "/api/config", &got)

	// The page greets the operator with "connected to <name> v<version>" from
	// this payload, so both fields have to be present here and not only on
	// /api/info.
	if got.Name != "StreamTest" {
		t.Errorf("name = %q, want StreamTest", got.Name)
	}
	if got.Version == "" {
		t.Error("version must not be empty")
	}

	if got.Limits.MaxDuration != 5 {
		t.Errorf("max_duration = %d, want 5", got.Limits.MaxDuration)
	}
	if got.Limits.MinInterval != 10 {
		t.Errorf("min_interval = %d, want 10", got.Limits.MinInterval)
	}
	if got.Limits.MaxPayloadSize != 64<<10 {
		t.Errorf("max_payload_size = %d, want %d", got.Limits.MaxPayloadSize, 64<<10)
	}
	if got.Limits.MaxConcurrentStreams != lim.MaxConcurrentStreams {
		t.Errorf("max_concurrent_streams = %d, want %d", got.Limits.MaxConcurrentStreams, lim.MaxConcurrentStreams)
	}
	if got.Defaults.Duration != 1 || got.Defaults.Interval != 100 || got.Defaults.PayloadSize != 128 {
		t.Errorf("defaults = %+v", got.Defaults)
	}
}

func TestMetrics(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	// Run one short stream so that the counters move.
	resp, err := http.Get(stack.URL("/api/stream/http?duration=1&interval=500&payload_size=0"))
	if err != nil {
		t.Fatalf("GET stream: %v", err)
	}
	_, _ = io.Copy(io.Discard, resp.Body)
	resp.Body.Close()

	var got struct {
		StreamsStarted  uint64 `json:"streams_started"`
		StreamsFinished uint64 `json:"streams_finished"`
		FramesSent      uint64 `json:"frames_sent"`
		BytesSent       uint64 `json:"bytes_sent"`
		ActiveStreams   int64  `json:"active_streams"`
	}
	getJSON(t, stack, "/api/metrics", &got)

	if got.StreamsStarted != 1 || got.StreamsFinished != 1 {
		t.Errorf("streams started/finished = %d/%d, want 1/1", got.StreamsStarted, got.StreamsFinished)
	}
	if got.FramesSent != 2 {
		t.Errorf("frames_sent = %d, want 2", got.FramesSent)
	}
	if got.BytesSent == 0 {
		t.Error("bytes_sent must be greater than zero")
	}
	if got.ActiveStreams != 0 {
		t.Errorf("active_streams = %d, want 0", got.ActiveStreams)
	}
}

func TestUnknownEndpointReturnsJSON404(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	resp, err := http.Get(stack.URL("/api/does-not-exist"))
	if err != nil {
		t.Fatalf("GET: %v", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusNotFound {
		t.Fatalf("status = %d, want 404", resp.StatusCode)
	}
	var body errorBody
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if body.Error != httpx.CodeNotFound {
		t.Fatalf("error = %q, want %q", body.Error, httpx.CodeNotFound)
	}
}

func TestWrongMethodReturns405(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	resp, err := http.Post(stack.URL("/api/health"), "application/json", strings.NewReader("{}"))
	if err != nil {
		t.Fatalf("POST: %v", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusMethodNotAllowed {
		t.Fatalf("status = %d, want 405", resp.StatusCode)
	}
	if allow := resp.Header.Get("Allow"); !strings.Contains(allow, "GET") {
		t.Fatalf("Allow = %q, want it to contain GET", allow)
	}
}

func TestStreamEndpointsRejectInvalidParams(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	endpoints := []string{"/api/stream/http", "/api/stream/sse", "/api/stream/ws"}
	queries := []struct {
		query string
		param string
	}{
		{"duration=abc", "duration"},
		{"duration=0", "duration"},
		{"duration=99999", "duration"},
		{"interval=abc", "interval"},
		{"interval=1", "interval"},
		{"interval=99999999", "interval"},
		{"payload_size=abc", "payload_size"},
		{"payload_size=-1", "payload_size"},
		{"payload_size=99999999", "payload_size"},
	}

	for _, endpoint := range endpoints {
		for _, tc := range queries {
			t.Run(endpoint+"?"+tc.query, func(t *testing.T) {
				resp, err := http.Get(stack.URL(endpoint + "?" + tc.query))
				if err != nil {
					t.Fatalf("GET: %v", err)
				}
				defer resp.Body.Close()

				if resp.StatusCode != http.StatusBadRequest {
					body, _ := io.ReadAll(io.LimitReader(resp.Body, 4096))
					t.Fatalf("status = %d, want 400 (body %s)", resp.StatusCode, body)
				}
				var body errorBody
				if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
					t.Fatalf("decode: %v", err)
				}
				if body.Error != httpx.CodeInvalidParameter {
					t.Errorf("error = %q, want %q", body.Error, httpx.CodeInvalidParameter)
				}
				if body.Param != tc.param {
					t.Errorf("param = %q, want %q", body.Param, tc.param)
				}
				if body.Message == "" {
					t.Error("message must explain the failure")
				}
			})
		}
	}
}

func TestStreamEndpointsAcceptBoundaryParams(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	// max duration, min interval, max payload: all legal.
	resp, err := http.Get(stack.URL("/api/stream/http?duration=5&interval=10&payload_size=65536"))
	if err != nil {
		t.Fatalf("GET: %v", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status = %d, want 200", resp.StatusCode)
	}
	// Read one frame, then hang up so the test does not wait five seconds.
	if _, err := bufio.NewReader(resp.Body).ReadString('\n'); err != nil {
		t.Fatalf("read frame: %v", err)
	}
}

func TestConcurrentStreamLimitIsEnforced(t *testing.T) {
	lim := testsupport.FastLimits()
	lim.MaxConcurrentStreams = 1
	lim.MaxDuration = time.Minute
	stack := testsupport.New(lim, nil)
	defer stack.Close()

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	req, err := http.NewRequestWithContext(ctx, http.MethodGet,
		stack.URL("/api/stream/http?duration=30&interval=100&payload_size=0"), nil)
	if err != nil {
		t.Fatalf("NewRequest: %v", err)
	}
	first, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatalf("first stream: %v", err)
	}
	defer first.Body.Close()

	if _, err := bufio.NewReader(first.Body).ReadString('\n'); err != nil {
		t.Fatalf("read first frame: %v", err)
	}
	testsupport.WaitFor(t, "the first stream to hold the only slot", 3*time.Second, func() bool {
		return stack.Manager.Active() == 1
	})

	// Every protocol must be rejected while the budget is exhausted.
	for _, endpoint := range []string{"/api/stream/http", "/api/stream/sse", "/api/stream/ws"} {
		resp, err := http.Get(stack.URL(endpoint + "?duration=5&interval=100"))
		if err != nil {
			t.Fatalf("GET %s: %v", endpoint, err)
		}
		body, _ := io.ReadAll(io.LimitReader(resp.Body, 4096))
		resp.Body.Close()

		if resp.StatusCode != http.StatusServiceUnavailable {
			t.Fatalf("%s status = %d, want 503 (body %s)", endpoint, resp.StatusCode, body)
		}
		var eb errorBody
		if err := json.Unmarshal(body, &eb); err != nil {
			t.Fatalf("decode %s: %v (%s)", endpoint, err, body)
		}
		if eb.Error != httpx.CodeTooManyStreams {
			t.Errorf("%s error = %q, want %q", endpoint, eb.Error, httpx.CodeTooManyStreams)
		}
		if resp.Header.Get("Retry-After") == "" {
			t.Errorf("%s must advertise Retry-After", endpoint)
		}
	}

	cancel()
	testsupport.WaitFor(t, "the slot to be released", 5*time.Second, func() bool {
		return stack.Manager.Active() == 0
	})
}

func TestCORSDisabledByDefault(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	req, _ := http.NewRequest(http.MethodGet, stack.URL("/api/health"), nil)
	req.Header.Set("Origin", "https://example.com")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatalf("GET: %v", err)
	}
	defer resp.Body.Close()

	if got := resp.Header.Get("Access-Control-Allow-Origin"); got != "" {
		t.Fatalf("Access-Control-Allow-Origin = %q, want none", got)
	}
}

func TestCORSAllowList(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), []string{"https://example.com"})
	defer stack.Close()

	do := func(origin string) *http.Response {
		t.Helper()
		req, _ := http.NewRequest(http.MethodGet, stack.URL("/api/health"), nil)
		req.Header.Set("Origin", origin)
		resp, err := http.DefaultClient.Do(req)
		if err != nil {
			t.Fatalf("GET: %v", err)
		}
		return resp
	}

	allowed := do("https://example.com")
	allowed.Body.Close()
	if got := allowed.Header.Get("Access-Control-Allow-Origin"); got != "https://example.com" {
		t.Errorf("allowed origin header = %q", got)
	}
	if got := allowed.Header.Get("Vary"); !strings.Contains(got, "Origin") {
		t.Errorf("Vary = %q, want it to contain Origin", got)
	}

	denied := do("https://evil.example")
	denied.Body.Close()
	if got := denied.Header.Get("Access-Control-Allow-Origin"); got != "" {
		t.Errorf("denied origin header = %q, want none", got)
	}
}

func TestCORSWildcard(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), []string{"*"})
	defer stack.Close()

	req, _ := http.NewRequest(http.MethodGet, stack.URL("/api/health"), nil)
	req.Header.Set("Origin", "https://anything.example")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatalf("GET: %v", err)
	}
	defer resp.Body.Close()

	if got := resp.Header.Get("Access-Control-Allow-Origin"); got != "*" {
		t.Fatalf("Access-Control-Allow-Origin = %q, want *", got)
	}
}

func TestCORSPreflight(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), []string{"https://example.com"})
	defer stack.Close()

	req, _ := http.NewRequest(http.MethodOptions, stack.URL("/api/stream/sse"), nil)
	req.Header.Set("Origin", "https://example.com")
	req.Header.Set("Access-Control-Request-Method", "GET")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatalf("OPTIONS: %v", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusNoContent {
		t.Fatalf("status = %d, want 204", resp.StatusCode)
	}
	if got := resp.Header.Get("Access-Control-Allow-Methods"); !strings.Contains(got, "GET") {
		t.Fatalf("Access-Control-Allow-Methods = %q", got)
	}
	if got := resp.Header.Get("Access-Control-Max-Age"); got == "" {
		t.Fatal("Access-Control-Max-Age must be set")
	}
}

func TestResponseHeaderIsJSONForAllErrors(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	resp, err := http.Get(stack.URL("/api/stream/http?duration=nope"))
	if err != nil {
		t.Fatalf("GET: %v", err)
	}
	defer resp.Body.Close()

	if ct := resp.Header.Get("Content-Type"); !strings.HasPrefix(ct, "application/json") {
		t.Fatalf("Content-Type = %q, want application/json", ct)
	}
	var body errorBody
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		t.Fatalf("decode: %v", err)
	}
}

func TestAPIVersionConstantMatchesInfo(t *testing.T) {
	stack := testsupport.New(testsupport.FastLimits(), nil)
	defer stack.Close()

	var got struct {
		Version string `json:"version"`
	}
	getJSON(t, stack, "/api/info", &got)
	if got.Version != api.Version {
		t.Fatalf("version = %q, want %q", got.Version, api.Version)
	}
}
