package protocol

import (
	_ "embed"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"unicode/utf8"
)

// PayloadText is the document that every frame's payload is cut from by default.
//
// It is ordinary prose rather than a run of filler bytes for two reasons: the
// bytes on the wire then look like something a real streaming endpoint would
// send, and the client has something meaningful to display as it arrives.
//
// The document is sliced by byte offset. A stream advances its offset by one
// chunk per frame and the encoder wraps around the end, so an open ended test
// keeps producing text forever instead of running out.
//
//go:embed payload.txt
var PayloadText string

// payloadDocument is the document actually in use: PayloadText unless
// LoadDocument replaced it with a file. Every stream reads it without a lock, so
// it is only ever replaced during startup, before the server starts listening.
var payloadDocument = []byte(PayloadText)

// payloadASCII records whether every byte of the document is ASCII. An ASCII
// document needs no rune alignment, which keeps the hot path a single branch.
var payloadASCII = isASCII(payloadDocument)

// PayloadDocumentSize is the length of the document in bytes.
func PayloadDocumentSize() int { return len(payloadDocument) }

// isASCII reports whether b is plain ASCII, i.e. whether every byte is its own
// rune.
func isASCII(b []byte) bool {
	for _, c := range b {
		if c >= utf8.RuneSelf {
			return false
		}
	}
	return true
}

// LoadDocument replaces the embedded payload document with the contents of path.
// An empty or blank path keeps the embedded document and reports no error.
//
// A path that does not exist yet is seeded with the embedded document, creating
// any missing parent directories, and then loaded. That way a fresh checkout or
// a new deployment starts with a working, editable file at the configured path
// rather than failing, and later runs pick up whatever edits were made to it.
//
// The document has to be valid UTF-8 and must not be blank: frames are cut on
// rune boundaries and the client verifies that each one carried exactly
// payload_size bytes, so a document that cannot be cut cleanly, or that is
// empty, would make every frame fail verification.
func LoadDocument(path string) error {
	path = strings.TrimSpace(path)
	if path == "" {
		return nil
	}

	info, err := os.Stat(path)
	if errors.Is(err, os.ErrNotExist) {
		// The file is not there yet: create it from the embedded document so
		// this run, and every run after it, has something to read and edit.
		if err := seedDocument(path); err != nil {
			return err
		}
		info, err = os.Stat(path)
	}
	if err != nil {
		return fmt.Errorf("payload document: %w", err)
	}
	if info.IsDir() {
		// Docker creates a directory when a bind mount's source file does not
		// exist, so this is a common and otherwise baffling failure. A real
		// directory is not something we can seed a file over, so it stays an
		// error rather than being silently replaced.
		return fmt.Errorf("payload document %s is a directory; a bind mount whose source file is missing creates one", path)
	}

	data, err := os.ReadFile(path)
	if err != nil {
		return fmt.Errorf("payload document: %w", err)
	}
	if !utf8.Valid(data) {
		return fmt.Errorf("payload document %s is not valid UTF-8", path)
	}
	if strings.TrimSpace(string(data)) == "" {
		return fmt.Errorf("payload document %s is empty", path)
	}

	payloadDocument = data
	payloadASCII = isASCII(data)
	return nil
}

// seedDocument writes the embedded document to path, creating any missing parent
// directories. It is called when path does not exist yet so that a run always
// finds a usable, editable file where the operator pointed PAYLOAD_FILE.
func seedDocument(path string) error {
	if dir := filepath.Dir(path); dir != "" {
		if err := os.MkdirAll(dir, 0o755); err != nil {
			return fmt.Errorf("payload document: creating directory for %s: %w", path, err)
		}
	}
	if err := os.WriteFile(path, []byte(PayloadText), 0o644); err != nil {
		return fmt.Errorf("payload document: seeding %s: %w", path, err)
	}
	return nil
}

// PayloadDocument returns the document currently in use.
func PayloadDocument() string { return string(payloadDocument) }

const hexDigits = "0123456789abcdef"

// appendEscaped appends src onto dst with JSON string escaping applied.
//
// Payload text is prose, so in practice the only byte that needs escaping is the
// newline separating two paragraphs. The remaining cases are handled anyway:
// loading a document must not be able to produce invalid JSON.
func appendEscaped(dst, src []byte) []byte {
	for _, c := range src {
		switch {
		case c == '"' || c == '\\':
			dst = append(dst, '\\', c)
		case c == '\n':
			dst = append(dst, '\\', 'n')
		case c == '\r':
			dst = append(dst, '\\', 'r')
		case c == '\t':
			dst = append(dst, '\\', 't')
		case c < 0x20:
			dst = append(dst, '\\', 'u', '0', '0', hexDigits[c>>4], hexDigits[c&0x0f])
		default:
			dst = append(dst, c)
		}
	}
	return dst
}

// appendDocumentSlice appends size bytes of the document starting at offset,
// wrapping around the end as many times as needed, with JSON escaping applied.
//
// The escaping happens here rather than on a precomputed copy of the document
// because a chunk boundary can fall in the middle of an escape sequence, which
// would produce JSON that no decoder could read.
func appendDocumentSlice(dst []byte, offset, size int) []byte {
	n := len(payloadDocument)
	if n == 0 || size <= 0 {
		return dst
	}

	start := offset % n
	if start < 0 {
		start += n
	}

	for remaining := size; remaining > 0; {
		chunk := n - start
		if chunk > remaining {
			chunk = remaining
		}
		dst = appendEscaped(dst, payloadDocument[start:start+chunk])
		remaining -= chunk
		start = 0
	}
	return dst
}

// consumedLength returns how much of the document one frame may carry when the
// caller asked for size bytes: at most size, and always a whole number of runes.
//
// Cutting a multi-byte rune in half would make Go's byte count and JavaScript's
// UTF-16 length disagree, and encoding/json would replace the torn sequence with
// U+FFFD, so every frame has to end on a rune boundary. For an ASCII document
// the answer is simply size, which is the common case.
func consumedLength(offset, size int) int {
	n := len(payloadDocument)
	if n == 0 || size <= 0 {
		return 0
	}
	if payloadASCII {
		return size
	}

	start := offset % n
	if start < 0 {
		start += n
	}

	consumed := 0
	for consumed < size {
		width := runeWidth(start + consumed)
		if consumed+width > size {
			break
		}
		consumed += width
	}
	if consumed == 0 {
		// The frame is narrower than a single rune of this document. Send one
		// whole rune rather than an empty payload, and let payload_size report
		// what was written: the client checks the two against each other, not
		// against the request.
		return runeWidth(start)
	}
	return consumed
}

// runeWidth returns the encoded width of the rune that starts at i, reading
// cyclically past the end of the document.
func runeWidth(i int) int {
	n := len(payloadDocument)
	if payloadDocument[i%n] < utf8.RuneSelf {
		return 1
	}
	// Copying at most four bytes into a small window is the cheapest way to
	// decode a rune that straddles the wrap point.
	var window [utf8.UTFMax]byte
	for k := range window {
		window[k] = payloadDocument[(i+k)%n]
	}
	_, width := utf8.DecodeRune(window[:])
	if width <= 0 {
		return 1
	}
	return width
}
