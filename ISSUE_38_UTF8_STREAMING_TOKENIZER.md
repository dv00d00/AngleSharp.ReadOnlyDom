# Issue #38: monotonic UTF-8 streaming tokenizer exploration

## Feasibility verdict

Parsing HTML incrementally from UTF-8 wire bytes is feasible. The tokenizer does not fundamentally require a retained
source or arbitrary rewind.

AngleSharp 1.5.2's `HtmlTokenizer` uses source rewind as a convenient implementation of spec reconsume:

- most `Back()` calls move one UTF-16 character;
- failed numeric character references move two, or three for a hexadecimal prefix;
- script end-tag candidates move `tagName.Length + 3`, which is theoretically unbounded because tag names are
  unbounded.

The large rewind is avoidable: retain the tentative `</name` candidate in tokenizer state until it is accepted or
emitted as text. The exploratory tokenizer therefore consumes source monotonically and reports zero source lookbehind.

The remaining buffering requirements are different from lookbehind:

- at most three trailing bytes for a UTF-8 scalar split across pipe segments;
- up to 32 ASCII bytes for AngleSharp-compatible named-character-reference lookup;
- an individual tag name, attribute value, comment, or malformed token can be unbounded by the HTML grammar, so a
  reusable buffer must be allowed to grow for pathological tokens;
- byte-level encoding sniffing may retain an initial prefix before committing the encoding. The UTF-8-only wire API can
  instead require transport/BOM policy to be settled by its caller.

## Prototype shape

`Utf8HtmlTokenizer` scans ASCII HTML syntax directly from `ReadOnlySpan<byte>` and `PipeReader`. Its synchronous sink
receives borrowed UTF-8 spans and must consume them before returning. Plain source text is forwarded without decoding or
copying. Reusable buffers hold tag names, attribute names and values, comments, and end-tag candidates only while those
tokens are incomplete.

`PipeReader.AdvanceTo(buffer.End)` is safe after each read because partial lexical state has already moved into the
tokenizer's reusable buffers. The pipe does not retain the whole response. UTF-16 decoding is deferred until a consumer
actually requests a string.

This API intentionally emits a start tag in three phases: tag name, zero or more attributes, and tag end. A read-only
construction sink can inspect every attribute but copy only the selected subset.

## Current correctness evidence

- contiguous input and every-byte segmentation produce identical event streams;
- a real `PipeReader` path produces the same event stream;
- multibyte UTF-8 split at arbitrary boundaries is reassembled before reaching the sink;
- curated data, tag, attribute, entity, RCDATA, raw-text, comment, and malformed nesting cases match AngleSharp lexical
  tokens;
- the focused tests pass, and the existing net10.0 suite remains green.

This is not yet spec-complete. Before integration it needs differential coverage and implementation for:

1. all script escaped and double-escaped substates;
2. complete DOCTYPE public/system and force-quirks behavior;
3. tree-builder-controlled CDATA acceptance in foreign content;
4. exact longest-prefix named-reference behavior and attribute exceptions;
5. duplicate-attribute error behavior and source positions;
6. invalid UTF-8 replacement and all HTML numeric-reference remappings;
7. tree-builder feedback for RCDATA, RAWTEXT, script, plaintext, and foreign-content modes rather than lexical heuristics.

Mutable browser behavior such as events, script execution, and `document.write` remains out of scope.

## Exploratory measurement

Command:

```powershell
./scripts/bench.ps1 utf8-tokenizer
```

Fixture: a 2,076,946-byte checked-in real-world page. The baseline starts from the same UTF-8 bytes, decodes the complete
string, then runs AngleSharp's tokenizer. The prototype consumes the UTF-8 bytes directly into a counting borrowed-span
sink.

| Method | Mean | Allocated |
| --- | ---: | ---: |
| Decode then AngleSharp tokenize | 24.91 ms | 18,109.78 KB |
| UTF-8 monotonic tokenize | 12.06 ms | 33.46 KB |

Artifact: `artifacts/benchmarks/20260713-224849-a8cf154-utf8-tokenizer`

The prototype is about 2.1 times faster and allocates about 99.8% less in this measurement. These are feasibility numbers,
not a replacement claim: the prototype currently implements less malformed-input and script behavior than AngleSharp.

## Recommendation

Continue, but keep it as an experimental UTF-8 tokenizer until the differential state matrix passes. Complete token
semantics first, then connect it to the existing read-only/compact construction sink. Do not copy the mutable DOM or
browser event surface. Benchmark the eventual wire-to-result path against both the current full-string route and the
existing construction-time extractor.
