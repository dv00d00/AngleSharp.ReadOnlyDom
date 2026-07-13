# Issue #17 streaming extraction prototype

## Decision

Keep the concrete construction-time view:

```text
first div#content -> owned normalized subtree text
```

It crosses the experiment's go gate against the compact extraction plan: 47.1% less allocation and 19.8% lower mean
time on the purpose-built fixture. This result justifies refining a small reusable view kernel. It does not justify a
generic query optimizer or arbitrary HTML-to-JSON fold.

Unsupported selectors, result shapes, navigation, diagnostics, and reusable unknown queries explicitly fall back to
`CompactExtractionPlan` over a materialized `CompactDocument`.

## API and execution model

```csharp
var plan = CompactStreamingExtractor.CompileFirstNormalizedText("div", "content");
var result = plan.Execute(html);
var ownedText = result.Value.ToString();
```

The compiled plan is reusable. Each execution creates fresh construction state, consumes the full input through
AngleSharp's tokenizer and HTML tree builder, projects the first matching element in final semantic preorder, returns an
owned string, and disposes the construction arena. No DOM escapes the parse.

The implementation deliberately keeps lightweight topology for all constructed nodes. Adoption-agency reconstruction,
foster parenting, templates, and foreign-content integration can reparent nodes nonlocally, so correct HTML construction
still needs the tree builder's mutation surface. The avoided costs are the frozen compact document, most attribute values,
early irrelevant text values, general query traversal state, and a retained DOM lifetime.

## Retention contract

- Always retain the requested `id` and attributes directly read by AngleSharp's tree builder: `type`, `action`, `prompt`,
  and `encoding`.
- Retain every attribute on active-formatting elements because reconstruction and the adoption-agency algorithm compare
  their complete attribute sets.
- Discard text values before the first syntactic target candidate. After a candidate appears, retain text values through
  end of input because later tree mutations can move them into the final target.
- Preserve whitespace-only text nodes for this view; they are required to normalize word boundaries correctly.
- Decode only values that survive tokenizer filtering or enter retained text.

Input is consumed to EOF and `EarlyTerminated` remains false. A matching close token is not proof that malformed later
input cannot mutate the candidate. Early termination remains unavailable until an insertion-mode-specific proof exists.

## Correctness pivots

Two attractive narrower strategies failed differential testing:

1. Retaining text only while the target was on the current ancestor stack failed after 8 generated malformed cases;
   later tree-builder mutations moved text into the target.
2. Reusing compact DOM's whitespace-only text elision failed after 4 generated cases because it removed a required word
   separator.

The implementation widened retention after the first candidate and made whitespace preservation view-specific. Crafted
cases cover entities, adoption-agency formatting, foster parenting and tables, template contents, SVG integration,
MathML `annotation-xml`, and formatting begun before the target. A 10,000-case malformed FsCheck corpus compares found
state and normalized text with AngleSharp's object DOM.

## Counters

Every result reports tokens processed, topology nodes materialized, attributes inspected, attributes retained, text
values retained, decoded values, UTF-8 input bytes consumed, and whether execution terminated early. These make the
retention behavior inspectable instead of describing the path as vaguely "streaming."

## Measurement

ShortRun, .NET 10, concurrent Server GC:

| Implementation | Mean | Allocated |
| --- | ---: | ---: |
| Read-only DOM plus traversal | 502.19 us | 158.88 KB |
| Compact DOM plus extraction plan | 592.81 us | 68.40 KB |
| Query-directed construction | 475.25 us | 36.20 KB |
| Naive token-close baseline | 18.26 us | 23.99 KB |

Against compact materialization, query-directed construction is 19.8% faster and allocates 47.1% less. Against the
read-only DOM path, it is 5.4% faster and allocates 77.2% less. The naive tokenizer path is only a speed ceiling: it does
not preserve HTML tree-construction semantics and is not a valid implementation.

The allocation result is strong; ShortRun timing confidence intervals are wide and should be rerun before making small
timing claims. The authoritative report is:

`artifacts/benchmarks/20260713-202704-17cb5e5-streaming/streaming/results/AngleSharp.ReadOnlyDom.Benchmarks.CompactStreamingExtractionBenchmark-report-github.md`

The experiment's gate was at least 30% allocation reduction or at least 20% throughput improvement versus compact, with
no material regression. The allocation result passes decisively; the mean-time result is just below the independent
timing threshold.

Final validation after formatting and counter verification:

- Release solution build: 0 warnings, 0 errors;
- .NET 10: 179,183 passed, 0 failed, 0 skipped;
- .NET Framework 4.7.2: 59,729 passed, 0 failed, 0 skipped;
- `git diff --check`: clean.

## Next boundary

Do not generalize arbitrary result shapes yet. The next useful proof is a second concrete view with meaningfully different
retention needs. Issue #7's LLM-oriented text extraction is a likely candidate and may become another view over the same
construction kernel, while keeping its semantic text policy separate from structured extraction.
