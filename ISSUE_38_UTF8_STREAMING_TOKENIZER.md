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

The canonical `Utf8HtmlTokenizer` now lives in `AngleSharp.ReadOnlyDom.Streaming`. It scans ASCII HTML syntax directly
from `ReadOnlySpan<byte>` and `PipeReader`. Its synchronous sink
receives borrowed UTF-8 spans and must consume them before returning. Plain source text is forwarded without decoding or
copying. Reusable buffers hold tag names, attribute names and values, comments, and end-tag candidates only while those
tokens are incomplete.

`PipeReader.AdvanceTo(buffer.End)` is safe after each read because partial lexical state has already moved into the
tokenizer's reusable buffers. The pipe does not retain the whole response. UTF-16 decoding is deferred until a consumer
actually requests a string.

This API intentionally emits a start tag in three phases: tag name, zero or more attributes, and tag end. A read-only
construction sink can inspect every attribute but copy only the selected subset.

`AngleSharp.ReadOnlyDom.Streaming.AngleSharp` is a separate compatibility assembly. It materializes AngleSharp struct
tokens and implements the proposed `IHtmlTokenSource` seam so AngleSharp's existing tree constructor remains the source
of truth for mutable-DOM construction. The native RODOM fold does not pass through this adapter and pays none of its
string, queue, or interface costs.

## Current correctness evidence

- contiguous input and every-byte segmentation produce identical event streams;
- a real `PipeReader` path produces the same event stream;
- multibyte UTF-8 split at arbitrary boundaries is reassembled before reaching the sink;
- invalid UTF-8 maximal parts are replaced before any borrowed callback;
- script escaped/double-escaped, plaintext, structured DOCTYPE, longest-prefix entities, attribute exceptions,
  duplicate-attribute first-wins, CRLF normalization, and numeric-reference overflow/remapping have differential tests;
- all 47 checked-in HTML documents (28,368,912 UTF-8 bytes) match AngleSharp's lexical event stream when contiguous and
  segmented at 1, 7, and 4096 bytes;
- the existing net10.0 suite remains green (179,221 tests on the latest run).

This is not yet a production replacement. Remaining gates are:

1. exhaust the html5lib tokenizer vectors, especially malformed script EOF and DOCTYPE recovery states;
2. report duplicate attributes and other tokenizer parse errors with source positions compatible with the existing
   parser;
3. differential-test mutable DOM observables for tables/foster parenting, adoption agency, templates, SVG/MathML,
   foreign content, and malformed nesting;
4. add cancellation, backpressure, maximum-token/resource policy, fuzzing, and adversarial segment-boundary gates;
5. measure the integrated raw-network `PipeReader` path and eliminate avoidable per-document workspace churn.

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
| Decode then AngleSharp tokenize | 20.35 ms | 18,332.67 KB |
| Validated UTF-8 monotonic tokenize | 16.13 ms | 17.58 KB |

Latest direct BenchmarkDotNet report:
`artifacts/benchmarks/20260714-213111-8db73d3-utf8-tokenizer/utf8-tokenizer/results/AngleSharp.ReadOnlyDom.Benchmarks.Utf8TokenizerBenchmark-report-github.md`

The semantically hardened tokenizer is about 21% faster and allocates about 99.9% less in this short run. Its maximum
simultaneously buffered token data was 6,754 bytes on the fixture. A temporary regression to 109.88 KB was traced to
`char.ConvertFromUtf32` allocating a UTF-16 string for each numeric character reference; encoding the validated scalar
directly with `Rune.EncodeToUtf8` and using AngleSharp's memory-backed entity lookup reduced the result to 17.58 KB.
These remain feasibility numbers, not a replacement
claim.

## Recommendation

Keep the UTF-8 tokenizer and its native borrowed-span sink in RODOM. Upstream only the small AngleSharp tree-construction
seam: `IHtmlTokenSource`, a concrete `HtmlParser.ParseDocumentAsync` overload, and public struct-token factories needed
by external implementations. Maintain the materializing AngleSharp bridge as a separate RODOM compatibility assembly.
This keeps the upstream diff reviewable, preserves AngleSharp's existing tree constructor, and lets RODOM evolve its
zero-copy fold without coupling that hot path to AngleSharp's mutable-DOM token representation.
