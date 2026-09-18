// Package protocol defines the StreamFrame wire model that is shared by the
// HTTP streaming (NDJSON), Server-Sent Events and WebSocket endpoints.
//
// The same logical frame is transported by all three protocols so that a single
// client-side metrics implementation can consume any of them:
//
//	{"sequence":1,"server_time":1730000000123,"payload_size":80}
//
// When payload_size > 0 the frame additionally carries a "payload" field holding
// that many bytes of the payload document (see payload.go). Shipping real
// payload bytes is what makes the throughput numbers produced by the tester
// meaningful; the client can also verify payload length to detect truncation
// independently of timing.
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

// Encoder renders frames as compact JSON into a caller supplied buffer so that
// the streaming hot path performs no per-frame allocations.
//
// An Encoder is immutable after construction and safe for concurrent use, so it
// does not hold the cursor into the payload document: that belongs to a single
// stream and is passed to Append on every frame.
type Encoder struct {
	size int
}

// NewEncoder builds an Encoder for a fixed payload size in bytes.
func NewEncoder(payloadSize int) *Encoder {
	if payloadSize < 0 {
		payloadSize = 0
	}
	return &Encoder{size: payloadSize}
}

// PayloadSize returns the number of payload bytes carried by every frame this
// encoder produces.
func (e *Encoder) PayloadSize() int { return e.size }

// Append renders the next frame onto dst and returns the extended slice together
// with the number of payload document bytes the frame consumed, which is what
// the caller has to advance its offset by. The produced bytes are exactly the
// JSON encoding of the equivalent Frame.
//
// offset selects the byte of the payload document this frame starts at; it is
// reduced modulo the document length, so a stream can advance it by the returned
// count and let it wrap. Frames with payload_size 0 carry no payload field, so
// they consume nothing and ignore offset.
//
// The returned count can differ from PayloadSize() in one case: when the
// document contains multi-byte runes the frame ends at the last whole rune that
// fits, and the payload_size field carries what was actually written.
func (e *Encoder) Append(dst []byte, sequence uint64, serverTime int64, offset int) ([]byte, int) {
	consumed := consumedLength(offset, e.size)

	dst = append(dst, `{"sequence":`...)
	dst = strconv.AppendUint(dst, sequence, 10)
	dst = append(dst, `,"server_time":`...)
	dst = strconv.AppendInt(dst, serverTime, 10)
	dst = append(dst, `,"payload_size":`...)
	dst = strconv.AppendInt(dst, int64(consumed), 10)
	if consumed > 0 {
		dst = append(dst, `,"payload":"`...)
		dst = appendDocumentSlice(dst, offset, consumed)
		dst = append(dst, '"')
	}
	return append(dst, '}'), consumed
}

// Encoder cache.
//
// An encoder is immutable and holds nothing but its payload size, so the cache
// exists to hand the same value to every stream that asks for the same size
// rather than to save memory. The entry cap keeps a caller iterating over
// arbitrary payload sizes from growing the map without bound.
const maxCachedEncoders = 32

var encoderCache = struct {
	mu     sync.Mutex
	bySize map[int]*Encoder
}{
	bySize: make(map[int]*Encoder),
}

// EncoderFor returns a (possibly cached) Encoder for the given payload size.
func EncoderFor(payloadSize int) *Encoder {
	if payloadSize < 0 {
		payloadSize = 0
	}
	encoderCache.mu.Lock()
	defer encoderCache.mu.Unlock()
	if e, ok := encoderCache.bySize[payloadSize]; ok {
		return e
	}
	e := NewEncoder(payloadSize)
	if len(encoderCache.bySize) < maxCachedEncoders {
		encoderCache.bySize[payloadSize] = e
	}
	return e
}
