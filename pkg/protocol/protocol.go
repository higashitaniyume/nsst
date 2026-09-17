// Package protocol defines the StreamFrame wire model that is shared by the
// HTTP streaming (NDJSON), Server-Sent Events and WebSocket endpoints.
//
// The same logical frame is transported by all three protocols so that a single
// client-side metrics implementation can consume any of them:
//
//	{"sequence":1,"server_time":1730000000123,"payload_size":4096}
//
// When payload_size > 0 the frame additionally carries a "payload" field whose
// UTF-8 length is exactly payload_size. Shipping real payload bytes is what makes
// the throughput numbers produced by the tester meaningful; the client can also
// verify payload length to detect truncation independently of timing.
package protocol

import (
	"encoding/json"
	"fmt"
	"strconv"
	"sync"
)

// Field names are part of the public wire contract. Do not rename them.
const (
	FieldSequence    = "sequence"
	FieldServerTime  = "server_time"
	FieldPayloadSize = "payload_size"
	FieldPayload     = "payload"
)

// Frame is the logical unit streamed by every endpoint.
//
//	Sequence    monotonically increasing per stream, starting at 1
//	ServerTime  Unix timestamp in milliseconds when the server produced the frame
//	PayloadSize number of payload bytes carried by this frame
//	Payload     filler bytes (omitted when PayloadSize == 0)
type Frame struct {
	Sequence    uint64 `json:"sequence"`
	ServerTime  int64  `json:"server_time"`
	PayloadSize int    `json:"payload_size"`
	Payload     string `json:"payload,omitempty"`
}

// Validate reports whether the frame is internally consistent.
func (f Frame) Validate() error {
	if f.Sequence == 0 {
		return fmt.Errorf("protocol: frame sequence must start at 1")
	}
	if f.ServerTime <= 0 {
		return fmt.Errorf("protocol: frame server_time must be positive")
	}
	if f.PayloadSize < 0 {
		return fmt.Errorf("protocol: frame payload_size must not be negative")
	}
	if len(f.Payload) != f.PayloadSize {
		return fmt.Errorf("protocol: payload length %d does not match payload_size %d", len(f.Payload), f.PayloadSize)
	}
	return nil
}

// Decode parses a single JSON frame.
func Decode(b []byte) (Frame, error) {
	var f Frame
	if err := json.Unmarshal(b, &f); err != nil {
		return Frame{}, fmt.Errorf("protocol: decode frame: %w", err)
	}
	return f, nil
}

// Encode renders a frame with encoding/json. It is the reference implementation;
// the server hot path uses Encoder.Append instead.
func Encode(f Frame) ([]byte, error) {
	b, err := json.Marshal(f)
	if err != nil {
		return nil, fmt.Errorf("protocol: encode frame: %w", err)
	}
	return b, nil
}

// fillByte is the byte used to pad a frame's payload. It is chosen so that the
// JSON encoding needs no escaping, which keeps the wire size predictable.
const fillByte = 'a'

// Encoder renders frames as compact JSON into a caller supplied buffer so that
// the streaming hot path performs no per-frame allocations.
//
// An Encoder is immutable after construction and safe for concurrent use.
type Encoder struct {
	size   int
	suffix []byte // `,"payload":"aaaa...a"` or nil when size == 0
}

// NewEncoder builds an Encoder for a fixed payload size in bytes.
func NewEncoder(payloadSize int) *Encoder {
	if payloadSize < 0 {
		payloadSize = 0
	}
	e := &Encoder{size: payloadSize}
	if payloadSize > 0 {
		// `,"payload":"` + payloadSize fill bytes + closing quote
		buf := make([]byte, 0, payloadSize+14)
		buf = append(buf, ',', '"', 'p', 'a', 'y', 'l', 'o', 'a', 'd', '"', ':', '"')
		for i := 0; i < payloadSize; i++ {
			buf = append(buf, fillByte)
		}
		buf = append(buf, '"')
		e.suffix = buf
	}
	return e
}

// PayloadSize returns the number of payload bytes carried by every frame this
// encoder produces.
func (e *Encoder) PayloadSize() int { return e.size }

// Append renders the next frame onto dst and returns the extended slice. The
// produced bytes are exactly the JSON encoding of the equivalent Frame.
func (e *Encoder) Append(dst []byte, sequence uint64, serverTime int64) []byte {
	dst = append(dst, `{"sequence":`...)
	dst = strconv.AppendUint(dst, sequence, 10)
	dst = append(dst, `,"server_time":`...)
	dst = strconv.AppendInt(dst, serverTime, 10)
	dst = append(dst, `,"payload_size":`...)
	dst = strconv.AppendInt(dst, int64(e.size), 10)
	dst = append(dst, e.suffix...)
	return append(dst, '}')
}

// Encoder cache.
//
// Building the filler buffer costs O(payload_size), so encoders are cached by
// size. The cache is deliberately bounded in both entries and bytes so that a
// caller iterating over arbitrary payload sizes cannot exhaust server memory.
const (
	maxCachedEncoders = 32
	maxCachedBytes    = 8 << 20 // 8 MiB of filler across all cached encoders
)

var encoderCache = struct {
	mu     sync.Mutex
	bySize map[int]*Encoder
	total  int
}{
	bySize: make(map[int]*Encoder),
}

// EncoderFor returns a (possibly cached) Encoder for the given payload size.
func EncoderFor(payloadSize int) *Encoder {
	if payloadSize < 0 {
		payloadSize = 0
	}
	encoderCache.mu.Lock()
	if e, ok := encoderCache.bySize[payloadSize]; ok {
		encoderCache.mu.Unlock()
		return e
	}
	encoderCache.mu.Unlock()

	e := NewEncoder(payloadSize)

	encoderCache.mu.Lock()
	defer encoderCache.mu.Unlock()
	if _, ok := encoderCache.bySize[payloadSize]; ok {
		// Lost the race; keep the already cached instance.
		return encoderCache.bySize[payloadSize]
	}
	// payloadSize bytes of filler plus a small amount of framing overhead.
	cost := payloadSize + 16
	if len(encoderCache.bySize) < maxCachedEncoders && encoderCache.total+cost <= maxCachedBytes {
		encoderCache.bySize[payloadSize] = e
		encoderCache.total += cost
	}
	return e
}
