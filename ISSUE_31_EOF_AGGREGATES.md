# Issue #31 EOF aggregate prototype

## Decision boundary

Prototype a limited C#-configured aggregate over final AngleSharp construction topology. Consume input through EOF,
produce only owned results, and dispose the arena without freezing or exposing a DOM.

This experiment covers:

- a first object or repeated top-level object rows;
- tag, ID, class-token, and attribute predicates;
- relative first-descendant field selection;
- attribute and normalized `TextContent` projections;
- deliberately minimal structural Markdown with subtree exclusions;
- direct `Utf8JsonWriter` output and a JSON string convenience method;
- requirements, execution explanation, ownership, and counters.

It intentionally does not cover parsed CSS/query syntax, nested arbitrary result graphs, reflection mapping, converters,
incremental row emission, `PipeReader`, backpressure, or complete HTML-to-Markdown compatibility. Unsupported shapes
continue to use `CompactExtractionPlan` over a materialized `CompactDocument`.

## Example

```csharp
var plan = CompactAggregate
    .ForEach(CompactAggregateSelector.Tag("article").WithClass("result"))
    .Field(
        "title",
        CompactAggregateProjection.FirstNormalizedText(CompactAggregateSelector.Tag("h2"))
    )
    .Field(
        "url",
        CompactAggregateProjection.FirstAttribute(CompactAggregateSelector.Tag("a"), "href"),
        required: true
    )
    .Field(
        "snippet",
        CompactAggregateProjection.FirstNormalizedText(
            CompactAggregateSelector.Tag("p").WithClass("snippet")
        )
    )
    .Compile();

var result = plan.Execute(html);
result.WriteJson(writer);
```

Descriptors and compiled plans are reusable. Each execution owns its strings; no value borrows from the disposed arena.

## Shared construction kernel

Issue #17 originally installed hooks for one hard-coded normalized-text view. This prototype replaces that coupling with
an internal construction-view definition/state contract. Both the specialized view and aggregate plan now receive node,
attribute, text-retention, token-count, and EOF-finalization hooks through the same seam.

The tokenizer retains only plan-required attributes plus attributes required by the HTML tree builder. All attributes on
active formatting elements remain available because reconstruction and the adoption-agency algorithm compare their full
sets. The aggregate currently retains all text values to EOF; on rooted strings these are normally source slices.

Normalized text remains whitespace-collapsed `TextContent`. It deliberately does not invent block separators, so adjacent
list items may concatenate. Markdown owns a separate structural boundary policy and currently supports headings,
paragraphs, line breaks, unordered list markers, emphasis, strong text, links, inline code, fenced preformatted text, and
explicit excluded subtrees.

## Correctness

Focused examples cover:

- one article projected simultaneously as fields, normalized text, Markdown, and JSON;
- repeated search-result objects;
- required empty versus missing attributes;
- documentation Markdown with navigation/advertisement exclusions and preserved code;
- malformed formatting adoption and foster-parenting cases against AngleSharp's object DOM.

A 10,000-case malformed FsCheck property compares the aggregate's selected scope and normalized text with the final
AngleSharp object DOM. Existing issue #17 differentials continue to exercise the same generalized construction seam.

## Performance

ShortRun, .NET 10, concurrent Server GC, using the issue #17 purpose-built fixture:

| Implementation | Mean | Allocated |
| --- | ---: | ---: |
| Read-only DOM plus traversal | 498.56 us | 158.88 KB |
| Compact DOM plus extraction plan | 599.71 us | 68.40 KB |
| Specialized query-directed construction | 479.33 us | 36.20 KB |
| EOF aggregate construction | 481.67 us | 36.33 KB |
| Naive token-close ceiling | 17.86 us | 23.99 KB |

The generalized EOF aggregate is 19.7% faster and allocates 46.9% less than compact materialization. It adds only 0.13 KB
allocation and 0.5% mean time versus the specialized issue #17 view on this fixture. ShortRun timing intervals remain
wide; allocation is the stronger result.

Authoritative local report:

`artifacts/benchmarks/20260713-211221-ab0229e-aggregate/streaming/results/AngleSharp.ReadOnlyDom.Benchmarks.CompactStreamingExtractionBenchmark-report-github.md`

## Streaming-input audit

This prototype is not bounded-memory input streaming. It uses a rooted string and EOF-finalized output.

AngleSharp's async stream path prefetches incrementally, but its current `WritableTextSource` retains all decoded input in
a `StringBuilder`; it can also retain raw bytes while encoding confidence is tentative, and `ReadMemory` produces strings.
The public generic custom-construction entry point used by the arena is synchronous. True bounded-memory input therefore
requires a separate sliding-buffer/token-ownership and async-tree-builder experiment.

The construction-view factory no longer reads `TextSource.Text` eagerly. It recognizes an existing `StringTextSource`
identity without forcing a full copy, preserving a clean seam for future source implementations.

## Validation

- Release solution build: 0 errors; existing benchmark/test analyzer warnings remain unchanged;
- .NET 10: 179,190 passed, 0 failed, 0 skipped;
- .NET Framework 4.7.2: 59,729 passed, 0 failed, 0 skipped;
- `git diff --check`: clean.
