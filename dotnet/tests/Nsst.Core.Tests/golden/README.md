# Golden fixtures

Both files here are **generated, not hand written**, and both are **frozen
reference data**: they pin behaviour the server has to keep reproducing, and the
tests compare against them rather than against another implementation.

| File | Covers | Cases |
| --- | --- | --- |
| `frames.txt` | the frame encoder: wire bytes for a given document, size, offset, sequence and timestamp | 975 |
| `params.txt` | request validation: which queries are accepted, and the exact text of every rejection | 50 |

## These can no longer be regenerated

They were produced by `dotnet/golden/gen`, a generator that drove the Go
implementation. That generator has been deleted along with the Go tree, so there
is **no longer a tool that can rewrite these files**.

That is a smaller loss than it sounds, and partly deliberate. A fixture
regenerated from the code it is supposed to check cannot catch a change in that
code — it only records whatever the code now does. So the value here was always
in *not* regenerating casually.

The practical consequence, though, is real and should be stated plainly: if the
frame format or an error message changes on purpose, these files have to be
updated by hand, and the diff reviewed line by line. A diff appearing without
someone intending it means something broke.

## frames.txt

### Format

```
doc  <name> bytes=<n> sha256=<hex> [hex=<document bytes>]
case <doc> payload=<n> offset=<n> seq=<n> time=<n> written=<n> consumed=<n> hex=<frame bytes>
```

Frames are hex encoded so the fixture itself cannot introduce an escaping
disagreement: the comparison is over bytes, not over how a text file represents
them.

### Why there are two documents

The server's built-in document
(`dotnet/src/Nsst.Core/Protocol/payload.txt`) is ASCII, 8699 bytes, and contains
exactly 35 escapable bytes — all of them newlines (`\n`). Measured: zero `\r`,
zero `\t`, zero `"`, zero `\`, zero other control bytes. So it exercises the `\n`
escape rule and the ASCII fast path in `ConsumedLength`, and nothing else. In
particular it contains no `&`, `<` or `>`, so it cannot show whether the encoder
HTML-escapes those three.

A second document is therefore fed through `LoadDocument` to cover what the
default cannot:

- multi-byte runes of every width (2, 3 and 4 bytes), including ones that straddle
  the wrap point and ones a frame cut lands in the middle of
- every short escape: `\"`, `\\`, `\n`, `\r`, `\t`
- control bytes that need `\u00XX`
- `&`, `<` and `>` — deliberately **not** HTML-escaped, unlike a general purpose
  JSON serializer

The `embedded` document is recorded by hash only. The project embeds the very
same `Protocol/payload.txt`, so matching hashes prove the fixture was cut from
identical bytes without duplicating the text into the fixture.

## params.txt

### Format

```
ok   <query> duration_ms=<n> interval_ms=<n> payload_size=<n> expected_frames=<n>
err  <query> param=<name> value=<v> message=<full error text>
```

`message=` is always last because it contains spaces; every other field is
tab separated. The query is the raw, already percent-encoded query string, so the
decoding step is compared as well as the validation step.

These strings are contract, not diagnostics — the API layer puts them straight into
the JSON error body an operator reads when a script fails, so a wording change is a
user visible change.

### Two inputs deliberately excluded

Both implementations reject an out-of-range duration with HTTP 400, but the
original Go parser reported a bound that had nothing to do with the input: it
computed `time.Duration(seconds) * time.Second`, which wraps above 9223372036
seconds, so an absurdly large duration was reported as *too small*:

```
duration=9999999999 -> parameter "duration": must be at least 1 seconds (got "9999999999")
```

That tells an operator the opposite of what is wrong. This parser compares the
bound in whole seconds and reports `must not exceed 3600 seconds` instead.
Reproducing the wrap would have meant copying a bug, so those two inputs are
excluded from this fixture and pinned by
`ParamsParserTests.ReportsTheViolatedBoundForDurationsThatGoWouldWrap`.

## When this changes

A diff in `frames.txt` means the wire format changed. A diff in `params.txt` means
a valid request became invalid, a rejection started being accepted, or an error
message an operator may have scripted against was reworded. Both are breaking
changes, and since nothing regenerates these files any more, a diff can only be a
deliberate hand edit.
