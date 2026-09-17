package stream

import (
	"net/url"
	"testing"
	"time"
)

func TestParseParamsDefaults(t *testing.T) {
	lim := DefaultLimits()
	got, err := ParseParams(url.Values{}, lim)
	if err != nil {
		t.Fatalf("ParseParams: %v", err)
	}
	want := Params{
		Duration:    lim.DefaultDuration,
		Interval:    lim.DefaultInterval,
		PayloadSize: lim.DefaultPayloadSize,
	}
	if got != want {
		t.Fatalf("params = %+v, want %+v", got, want)
	}
}

func TestParseParamsBlankValuesFallBackToDefaults(t *testing.T) {
	lim := DefaultLimits()
	got, err := ParseParams(url.Values{"duration": {""}, "interval": {"  "}, "payload_size": {""}}, lim)
	if err != nil {
		t.Fatalf("ParseParams: %v", err)
	}
	if got.Duration != lim.DefaultDuration || got.Interval != lim.DefaultInterval || got.PayloadSize != lim.DefaultPayloadSize {
		t.Fatalf("blank params did not fall back to defaults: %+v", got)
	}
}

func TestParseParamsValues(t *testing.T) {
	lim := DefaultLimits()
	got, err := ParseParams(url.Values{
		"duration":     {"30"},
		"interval":     {"250"},
		"payload_size": {"65536"},
	}, lim)
	if err != nil {
		t.Fatalf("ParseParams: %v", err)
	}
	if got.Duration != 30*time.Second {
		t.Errorf("duration = %v, want 30s", got.Duration)
	}
	if got.Interval != 250*time.Millisecond {
		t.Errorf("interval = %v, want 250ms", got.Interval)
	}
	if got.PayloadSize != 65536 {
		t.Errorf("payload size = %d, want 65536", got.PayloadSize)
	}
}

func TestParseParamsRejectsBadInput(t *testing.T) {
	lim := DefaultLimits()
	tests := []struct {
		name  string
		query url.Values
		param string
	}{
		{"duration not a number", url.Values{"duration": {"abc"}}, "duration"},
		{"duration zero", url.Values{"duration": {"0"}}, "duration"},
		{"duration negative", url.Values{"duration": {"-1"}}, "duration"},
		{"duration above max", url.Values{"duration": {"99999"}}, "duration"},
		{"interval not a number", url.Values{"interval": {"x"}}, "interval"},
		{"interval zero", url.Values{"interval": {"0"}}, "interval"},
		{"interval below min", url.Values{"interval": {"1"}}, "interval"},
		{"interval above max", url.Values{"interval": {"99999999"}}, "interval"},
		{"payload not a number", url.Values{"payload_size": {"big"}}, "payload_size"},
		{"payload negative", url.Values{"payload_size": {"-1"}}, "payload_size"},
		{"payload above max", url.Values{"payload_size": {"99999999"}}, "payload_size"},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			_, err := ParseParams(tc.query, lim)
			if err == nil {
				t.Fatal("expected an error")
			}
			pe, ok := err.(*ParamError)
			if !ok {
				t.Fatalf("error type = %T, want *ParamError", err)
			}
			if pe.Param != tc.param {
				t.Fatalf("param = %q, want %q (%v)", pe.Param, tc.param, err)
			}
			if pe.Error() == "" {
				t.Fatal("error message must not be empty")
			}
		})
	}
}

func TestParseParamsAcceptsExactBounds(t *testing.T) {
	lim := DefaultLimits()
	got, err := ParseParams(url.Values{
		"duration":     {"3600"},
		"interval":     {"10"},
		"payload_size": {"1048576"},
	}, lim)
	if err != nil {
		t.Fatalf("boundary values must be accepted: %v", err)
	}
	if got.PayloadSize != 1<<20 {
		t.Fatalf("payload size = %d", got.PayloadSize)
	}
}

func TestParseParamsIgnoresUnknownParameters(t *testing.T) {
	lim := DefaultLimits()
	if _, err := ParseParams(url.Values{"nonsense": {"1"}}, lim); err != nil {
		t.Fatalf("unknown parameters must be ignored: %v", err)
	}
}

func TestParamsExpectedFrames(t *testing.T) {
	tests := []struct {
		duration time.Duration
		interval time.Duration
		want     int64
	}{
		{time.Second, 100 * time.Millisecond, 10},
		{time.Second, time.Second, 1},
		{time.Second, 60 * time.Second, 1},
		{1500 * time.Millisecond, 100 * time.Millisecond, 15},
		{250 * time.Millisecond, 100 * time.Millisecond, 3},
		{time.Second, 300 * time.Millisecond, 4},
	}
	for _, tc := range tests {
		p := Params{Duration: tc.duration, Interval: tc.interval}
		if got := p.ExpectedFrames(); got != tc.want {
			t.Errorf("ExpectedFrames(%v/%v) = %d, want %d", tc.duration, tc.interval, got, tc.want)
		}
	}
}

func TestParseParamsZeroPayloadIsAllowed(t *testing.T) {
	lim := DefaultLimits()
	got, err := ParseParams(url.Values{"payload_size": {"0"}}, lim)
	if err != nil {
		t.Fatalf("payload_size=0 must be allowed: %v", err)
	}
	if got.PayloadSize != 0 {
		t.Fatalf("payload size = %d, want 0", got.PayloadSize)
	}
}
