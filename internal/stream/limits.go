// Package stream contains the protocol independent streaming engine: parameter
// parsing and validation, frame pacing, the emit loop and the connection
// manager that enforces concurrency limits and graceful shutdown.
package stream

import (
	"fmt"
	"net/url"
	"strconv"
	"strings"
	"time"
)

// Limits describes the hard bounds a client request is validated against. Values
// are derived from environment variables by internal/config.
type Limits struct {
	MinDuration        time.Duration
	MaxDuration        time.Duration
	DefaultDuration    time.Duration
	MinInterval        time.Duration
	MaxInterval        time.Duration
	DefaultInterval    time.Duration
	MaxPayloadSize     int
	DefaultPayloadSize int

	MaxConcurrentStreams int

	// WriteTimeout bounds a single frame write. A client that stops reading is
	// disconnected once this expires, which is the main back pressure valve.
	WriteTimeout time.Duration
}

// DefaultLimits returns the documented defaults.
func DefaultLimits() Limits {
	return Limits{
		MinDuration:          time.Second,
		MaxDuration:          time.Hour,
		DefaultDuration:      time.Minute,
		MinInterval:          10 * time.Millisecond,
		MaxInterval:          time.Minute,
		DefaultInterval:      100 * time.Millisecond,
		MaxPayloadSize:       1 << 20, // 1 MiB
		DefaultPayloadSize:   4096,
		MaxConcurrentStreams: 100,
		WriteTimeout:         15 * time.Second,
	}
}

// Params is a validated client request.
type Params struct {
	Duration    time.Duration
	Interval    time.Duration
	PayloadSize int
}

// ExpectedFrames returns how many frames the server will emit for these params.
// Frames are sent at t=0, interval, 2*interval, ... while the elapsed time is
// strictly below Duration, i.e. ceil(Duration/Interval) frames.
func (p Params) ExpectedFrames() int64 {
	if p.Interval <= 0 {
		return 1
	}
	return int64((p.Duration + p.Interval - 1) / p.Interval)
}

// ParamError is returned for a malformed or out of range query parameter. The
// API layer maps it to HTTP 400.
type ParamError struct {
	Param  string
	Value  string
	Reason string
}

func (e *ParamError) Error() string {
	if e.Value == "" {
		return fmt.Sprintf("parameter %q %s", e.Param, e.Reason)
	}
	return fmt.Sprintf("parameter %q: %s (got %q)", e.Param, e.Reason, e.Value)
}

// ParseParams reads duration, interval and payload_size from the query string and
// validates them against lim. Missing or empty values fall back to the defaults.
func ParseParams(q url.Values, lim Limits) (Params, error) {
	p := Params{
		Duration:    lim.DefaultDuration,
		Interval:    lim.DefaultInterval,
		PayloadSize: lim.DefaultPayloadSize,
	}

	seconds, err := intParam(q, "duration")
	if err != nil {
		return Params{}, err
	}
	if seconds != nil {
		if *seconds <= 0 {
			return Params{}, &ParamError{"duration", strconv.Itoa(*seconds), "must be a positive number of seconds"}
		}
		if time.Duration(*seconds)*time.Second > lim.MaxDuration {
			return Params{}, &ParamError{
				Param:  "duration",
				Value:  strconv.Itoa(*seconds),
				Reason: fmt.Sprintf("must not exceed %d seconds", int(lim.MaxDuration/time.Second)),
			}
		}
		if time.Duration(*seconds)*time.Second < lim.MinDuration {
			return Params{}, &ParamError{
				Param:  "duration",
				Value:  strconv.Itoa(*seconds),
				Reason: fmt.Sprintf("must be at least %d seconds", int(lim.MinDuration/time.Second)),
			}
		}
		p.Duration = time.Duration(*seconds) * time.Second
	}

	millis, err := intParam(q, "interval")
	if err != nil {
		return Params{}, err
	}
	if millis != nil {
		if *millis <= 0 {
			return Params{}, &ParamError{"interval", strconv.Itoa(*millis), "must be a positive number of milliseconds"}
		}
		if d := time.Duration(*millis) * time.Millisecond; d < lim.MinInterval {
			return Params{}, &ParamError{
				Param:  "interval",
				Value:  strconv.Itoa(*millis),
				Reason: fmt.Sprintf("must be at least %d milliseconds", int(lim.MinInterval/time.Millisecond)),
			}
		}
		if d := time.Duration(*millis) * time.Millisecond; d > lim.MaxInterval {
			return Params{}, &ParamError{
				Param:  "interval",
				Value:  strconv.Itoa(*millis),
				Reason: fmt.Sprintf("must not exceed %d milliseconds", int(lim.MaxInterval/time.Millisecond)),
			}
		}
		p.Interval = time.Duration(*millis) * time.Millisecond
	}

	payload, err := intParam(q, "payload_size")
	if err != nil {
		return Params{}, err
	}
	if payload != nil {
		if *payload < 0 {
			return Params{}, &ParamError{"payload_size", strconv.Itoa(*payload), "must not be negative"}
		}
		if *payload > lim.MaxPayloadSize {
			return Params{}, &ParamError{
				Param:  "payload_size",
				Value:  strconv.Itoa(*payload),
				Reason: fmt.Sprintf("must not exceed %d bytes", lim.MaxPayloadSize),
			}
		}
		p.PayloadSize = *payload
	}

	return p, nil
}

// intParam reads an optional non-negative integer parameter. It returns (nil, nil)
// when the parameter is absent or blank so the caller can apply its default.
func intParam(q url.Values, name string) (*int, error) {
	if !q.Has(name) {
		return nil, nil
	}
	raw := strings.TrimSpace(q.Get(name))
	if raw == "" {
		return nil, nil
	}
	v, err := strconv.Atoi(raw)
	if err != nil {
		return nil, &ParamError{Param: name, Value: raw, Reason: "must be an integer"}
	}
	return &v, nil
}
