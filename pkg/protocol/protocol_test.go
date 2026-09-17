package protocol

import (
	"encoding/json"
	"strings"
	"testing"
)

func TestEncoderMatchesJSONEncoding(t *testing.T) {
	tests := []struct {
		name        string
		payloadSize int
	}{
		{"metadata only", 0},
		{"small payload", 16},
		{"default payload", 4096},
		{"large payload", 64 << 10},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			enc := NewEncoder(tc.payloadSize)

			for _, seq := range []uint64{1, 2, 42, 1 << 40} {
				const serverTime = int64(1730000000123)

				got := enc.Append(nil, seq, serverTime)

				// The hand rolled encoder must produce exactly what encoding/json
				// would produce for the equivalent Frame.
				want, err := Encode(Frame{
					Sequence:    seq,
					ServerTime:  serverTime,
					PayloadSize: tc.payloadSize,
					Payload:     strings.Repeat("a", tc.payloadSize),
				})
				if err != nil {
					t.Fatalf("Encode: %v", err)
				}
				if string(got) != string(want) {
					t.Fatalf("encoder output does not match encoding/json\n got %s\nwant %s", got, want)
				}

				frame, err := Decode(got)
				if err != nil {
					t.Fatalf("Decode(%s): %v", got, err)
				}
				if err := frame.Validate(); err != nil {
					t.Fatalf("Validate: %v", err)
				}
				if frame.Sequence != seq {
					t.Errorf("sequence = %d, want %d", frame.Sequence, seq)
				}
				if frame.ServerTime != serverTime {
					t.Errorf("server_time = %d, want %d", frame.ServerTime, serverTime)
				}
				if frame.PayloadSize != tc.payloadSize {
					t.Errorf("payload_size = %d, want %d", frame.PayloadSize, tc.payloadSize)
				}
				if len(frame.Payload) != tc.payloadSize {
					t.Errorf("payload bytes = %d, want %d", len(frame.Payload), tc.payloadSize)
				}
			}
		})
	}
}

func TestEncoderMetadataOnlyShape(t *testing.T) {
	// payload_size=0 must produce exactly the documented three field frame.
	got := string(NewEncoder(0).Append(nil, 1, 1730000000123))
	want := `{"sequence":1,"server_time":1730000000123,"payload_size":0}`
	if got != want {
		t.Fatalf("frame = %s, want %s", got, want)
	}
}

func TestAppendReusesBuffer(t *testing.T) {
	enc := NewEncoder(8)
	buf := make([]byte, 0, 256)
	first := enc.Append(buf, 1, 1000)

	// Appending into the same backing array must not grow it.
	second := enc.Append(first[:0], 2, 2000)
	if cap(second) != cap(first) {
		t.Fatalf("buffer grew: cap %d -> %d", cap(first), cap(second))
	}
	if !strings.Contains(string(second), `"sequence":2`) {
		t.Fatalf("frame not re-rendered: %s", second)
	}
}

func TestEncoderForCaches(t *testing.T) {
	a := EncoderFor(4096)
	b := EncoderFor(4096)
	if a != b {
		t.Fatal("EncoderFor did not return the cached encoder")
	}
	if a.PayloadSize() != 4096 {
		t.Fatalf("payload size = %d, want 4096", a.PayloadSize())
	}
	// A different size must not collide with the cached one.
	if c := EncoderFor(1024); c == a {
		t.Fatal("distinct payload sizes shared an encoder")
	}
}

func TestEncoderNegativeSizeIsClamped(t *testing.T) {
	enc := NewEncoder(-5)
	if enc.PayloadSize() != 0 {
		t.Fatalf("payload size = %d, want 0", enc.PayloadSize())
	}
}

func TestFrameValidateRejectsInconsistentFrames(t *testing.T) {
	tests := []struct {
		name  string
		frame Frame
	}{
		{"zero sequence", Frame{Sequence: 0, ServerTime: 1}},
		{"zero server time", Frame{Sequence: 1, ServerTime: 0}},
		{"negative payload size", Frame{Sequence: 1, ServerTime: 1, PayloadSize: -1}},
		{"payload mismatch", Frame{Sequence: 1, ServerTime: 1, PayloadSize: 10, Payload: "abc"}},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			if err := tc.frame.Validate(); err == nil {
				t.Fatal("expected validation error")
			}
		})
	}
}

func TestFrameJSONFieldNames(t *testing.T) {
	// The wire contract is public; guard the names against accidental renames.
	b, err := json.Marshal(Frame{Sequence: 7, ServerTime: 9, PayloadSize: 0})
	if err != nil {
		t.Fatal(err)
	}
	var raw map[string]any
	if err := json.Unmarshal(b, &raw); err != nil {
		t.Fatal(err)
	}
	for _, field := range []string{FieldSequence, FieldServerTime, FieldPayloadSize} {
		if _, ok := raw[field]; !ok {
			t.Errorf("field %q missing from %s", field, b)
		}
	}
	if _, ok := raw[FieldPayload]; ok {
		t.Errorf("payload must be omitted when empty: %s", b)
	}
}
