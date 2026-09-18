package protocol

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"unicode/utf8"
)

// documentChunkAt returns the size bytes of the document that start at offset,
// wrapping around the end, exactly as a client sees them after the JSON string
// has been decoded. It assumes the document can be cut on any byte boundary.
func documentChunkAt(offset, size int) string {
	n := len(payloadDocument)
	if n == 0 || size <= 0 {
		return ""
	}
	start := offset % n
	var b strings.Builder
	for remaining := size; remaining > 0; {
		chunk := n - start
		if chunk > remaining {
			chunk = remaining
		}
		b.WriteString(string(payloadDocument[start : start+chunk]))
		remaining -= chunk
		start = 0
	}
	return b.String()
}

func decodeFrame(t *testing.T, raw []byte) Frame {
	t.Helper()
	frame, err := Decode(raw)
	if err != nil {
		t.Fatalf("Decode(%s): %v", raw, err)
	}
	if err := frame.Validate(); err != nil {
		t.Fatalf("Validate(%s): %v", raw, err)
	}
	return frame
}

// frameAt renders one frame of enc at a document offset and returns the raw
// bytes with the number of document bytes the encoder consumed.
func frameAt(enc *Encoder, sequence uint64, offset int) ([]byte, int) {
	return enc.Append(nil, sequence, 1730000000123, offset)
}

// withDocument installs data as the payload document for one test and restores
// the embedded document afterwards.
func withDocument(t *testing.T, data string) {
	t.Helper()
	payloadDocument = []byte(data)
	payloadASCII = isASCII(payloadDocument)
	t.Cleanup(func() {
		payloadDocument = []byte(PayloadText)
		payloadASCII = isASCII(payloadDocument)
	})
}

// --------------------------------------------------------------- embedded doc

func TestPayloadDocumentIsPlainASCII(t *testing.T) {
	// The shipped document is ASCII so that Go's byte count and JavaScript's
	// UTF-16 length agree; a CRLF checkout of payload.txt would also double the
	// escaping, so both properties are pinned here.
	if PayloadText == "" {
		t.Fatal("payload document is empty")
	}
	for i := 0; i < len(PayloadText); i++ {
		c := PayloadText[i]
		if c >= utf8.RuneSelf {
			t.Fatalf("document byte %d is not ASCII: 0x%02x", i, c)
		}
		if c == '\r' {
			t.Fatalf("document byte %d is a carriage return: payload.txt must use LF endings", i)
		}
	}
	if len(PayloadText) < 4096 {
		t.Errorf("document is only %d bytes, too short to stay interesting", len(PayloadText))
	}
}

func TestChunkWrapsAroundEndOfDocument(t *testing.T) {
	n := PayloadDocumentSize()
	const size = 120
	const tail = 40
	offset := n - tail

	want := PayloadText[offset:] + PayloadText[:size-tail]
	if got := documentChunkAt(offset, size); got != want {
		t.Fatal("test helper disagrees with the expected wrap")
	}

	raw, consumed := frameAt(NewEncoder(size), 1, offset)
	if consumed != size {
		t.Fatalf("consumed = %d, want %d for an ASCII document", consumed, size)
	}
	frame := decodeFrame(t, raw)
	if frame.Payload != want {
		t.Fatalf("wrapped payload = %q, want %q", frame.Payload, want)
	}
	if frame.PayloadSize != size {
		t.Fatalf("payload_size = %d, want %d", frame.PayloadSize, size)
	}
}

func TestChunkLargerThanDocumentRepeatsIt(t *testing.T) {
	size := PayloadDocumentSize() + 25
	const offset = 10

	raw, _ := frameAt(NewEncoder(size), 1, offset)
	frame := decodeFrame(t, raw)
	if len(frame.Payload) != size {
		t.Fatalf("payload = %d bytes, want %d", len(frame.Payload), size)
	}
	if frame.Payload != documentChunkAt(offset, size) {
		t.Fatal("payload does not match the document read repeatedly")
	}
}

func TestEncoderEscapesParagraphBreaks(t *testing.T) {
	// A frame that straddles a paragraph break carries a newline. Writing it
	// literally would produce JSON that no decoder can read, so the encoder has
	// to escape it while still reporting the decoded length in payload_size.
	idx := strings.IndexByte(PayloadText, '\n')
	if idx < 0 {
		t.Fatal("document has no paragraph break to test with")
	}

	const size = 9
	offset := idx - 4

	raw, _ := frameAt(NewEncoder(size), 1, offset)
	if strings.ContainsAny(string(raw), "\n\r") {
		t.Fatalf("frame contains a raw control character: %q", raw)
	}

	frame := decodeFrame(t, raw)
	want := documentChunkAt(offset, size)
	if frame.Payload != want {
		t.Fatalf("payload = %q, want %q", frame.Payload, want)
	}
	if frame.PayloadSize != len(frame.Payload) {
		t.Fatalf("payload_size = %d, decoded payload = %d bytes", frame.PayloadSize, len(frame.Payload))
	}
}

func TestConsecutiveFramesContinueTheDocument(t *testing.T) {
	const size = 100
	enc := NewEncoder(size)

	var got strings.Builder
	offset := 0
	for seq := uint64(1); seq <= 5; seq++ {
		raw, consumed := frameAt(enc, seq, offset)
		got.WriteString(decodeFrame(t, raw).Payload)
		offset += consumed
	}

	if want := PayloadText[:5*size]; got.String() != want {
		t.Fatal("concatenated frames do not continue the document")
	}
}

func TestMetadataOnlyEncoderIgnoresOffset(t *testing.T) {
	raw, consumed := frameAt(NewEncoder(0), 3, 12345)
	if consumed != 0 {
		t.Fatalf("consumed = %d, want 0", consumed)
	}
	if got, want := string(raw), `{"sequence":3,"server_time":1730000000123,"payload_size":0}`; got != want {
		t.Fatalf("frame = %s, want %s", got, want)
	}
}

func TestNegativeOffsetIsWrapped(t *testing.T) {
	const size = 16
	raw, _ := frameAt(NewEncoder(size), 1, -5)
	if got, want := decodeFrame(t, raw).Payload, documentChunkAt(PayloadDocumentSize()-5, size); got != want {
		t.Fatalf("payload = %q, want %q", got, want)
	}
}

// ------------------------------------------------------------- multi byte text

func TestMultiByteDocumentEndsEveryFrameOnARuneBoundary(t *testing.T) {
	// Three bytes per rune means a frame cannot stop on an arbitrary byte: the
	// encoder has to give up the bytes that would have split a character. Every
	// frame still has to decode, report its true length, and lose nothing.
	doc := "网络流稳定性测试。每一帧都必须沿着字符边界切分，否则 Go 统计的字节数就会和 JavaScript 统计的长度不一致。" +
		"这一段用来验证外部载入的文档可以包含任意 UTF-8 字符。"
	withDocument(t, doc)

	const size = 20
	enc := NewEncoder(size)

	var got strings.Builder
	offset := 0
	for seq := uint64(1); seq <= 24; seq++ {
		raw, consumed := frameAt(enc, seq, offset)
		if consumed <= 0 || consumed > size {
			t.Fatalf("frame %d consumed %d bytes, want 1..%d", seq, consumed, size)
		}

		frame := decodeFrame(t, raw)
		if frame.PayloadSize != consumed {
			t.Fatalf("frame %d: payload_size = %d, consumed = %d", seq, frame.PayloadSize, consumed)
		}
		if frame.PayloadSize != len(frame.Payload) {
			t.Fatalf("frame %d: payload_size = %d but payload is %d bytes", seq, frame.PayloadSize, len(frame.Payload))
		}
		if !utf8.ValidString(frame.Payload) {
			t.Fatalf("frame %d payload is not valid UTF-8: %q", seq, frame.Payload)
		}

		got.WriteString(frame.Payload)
		offset += consumed
	}

	if want := doc + doc; !strings.HasPrefix(want, got.String()) {
		t.Fatalf("frames did not reproduce the document:\n got %q\nwant prefix of %q", got.String(), want)
	}
}

func TestChunkNarrowerThanOneRuneStillSendsTheRune(t *testing.T) {
	withDocument(t, "汉字")

	// Two bytes cannot hold a three byte rune, so the frame grows to one rune
	// and payload_size reports what was actually written.
	raw, consumed := frameAt(NewEncoder(2), 1, 0)
	if consumed != 3 {
		t.Fatalf("consumed = %d, want 3", consumed)
	}
	frame := decodeFrame(t, raw)
	if frame.Payload != "汉" || frame.PayloadSize != 3 {
		t.Fatalf("frame = %+v, want one three byte rune", frame)
	}
}

// ------------------------------------------------------------------- loader

func TestLoadDocumentReadsAFile(t *testing.T) {
	path := filepath.Join(t.TempDir(), "payload.txt")
	want := "从文件里读进来的正文。\n\n第二段。"
	if err := os.WriteFile(path, []byte(want), 0o600); err != nil {
		t.Fatal(err)
	}

	t.Cleanup(func() {
		payloadDocument = []byte(PayloadText)
		payloadASCII = isASCII(payloadDocument)
	})
	if err := LoadDocument(path); err != nil {
		t.Fatalf("LoadDocument: %v", err)
	}

	if got := PayloadDocument(); got != want {
		t.Fatalf("document = %q, want %q", got, want)
	}
	if payloadASCII {
		t.Error("a multi byte document must not be flagged ASCII")
	}

	raw, _ := frameAt(NewEncoder(9), 1, 0)
	frame := decodeFrame(t, raw)
	if !utf8.ValidString(frame.Payload) || frame.PayloadSize != len(frame.Payload) {
		t.Fatalf("frame from a loaded document is inconsistent: %+v", frame)
	}
}

func TestLoadDocumentWithBlankPathKeepsTheEmbeddedDocument(t *testing.T) {
	for _, path := range []string{"", "   "} {
		if err := LoadDocument(path); err != nil {
			t.Fatalf("LoadDocument(%q): %v", path, err)
		}
		if got := PayloadDocument(); got != PayloadText {
			t.Fatalf("LoadDocument(%q) replaced the embedded document", path)
		}
	}
}

func TestLoadDocumentRejectsUnusableFiles(t *testing.T) {
	dir := t.TempDir()

	invalid := filepath.Join(dir, "invalid.txt")
	if err := os.WriteFile(invalid, []byte{0xff, 0xfe, 0x00}, 0o600); err != nil {
		t.Fatal(err)
	}

	blank := filepath.Join(dir, "blank.txt")
	if err := os.WriteFile(blank, []byte("  \n\t\n"), 0o600); err != nil {
		t.Fatal(err)
	}

	empty := filepath.Join(dir, "empty.txt")
	if err := os.WriteFile(empty, nil, 0o600); err != nil {
		t.Fatal(err)
	}

	tests := []struct {
		name string
		path string
	}{
		{"missing file", filepath.Join(dir, "nope.txt")},
		{"directory", dir},
		{"invalid UTF-8", invalid},
		{"blank", blank},
		{"empty", empty},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			if err := LoadDocument(tc.path); err == nil {
				t.Fatal("expected an error")
			}
			if got := PayloadDocument(); got != PayloadText {
				t.Fatal("a failed load must leave the embedded document in place")
			}
		})
	}
}
