# AngleSharp.ReadOnlyDom

Parse HTML without paying for a fully mutable DOM when the result is only going to be read, queried, or folded into
another shape.

This repository explores a spectrum of representations built around AngleSharp's HTML semantics:

| Lane | Retained state | Best fit |
| --- | --- | --- |
| Read-only DOM | Familiar object graph | General navigation with lower allocation than the mutable DOM |
| Compact DOM | Pooled columnar document | Repeated queries and longer-lived parsed documents |
| UTF-8 stream query | Bounded parser/query state | Extracting typed rows, text, JSON, or Markdown directly from wire bytes |

The streaming lane is the main experimental direction: selectors are compiled into the tokenizer's target encoding,
only requested attributes and text are captured, and caller-owned state determines the result shape. It supports
contiguous UTF-8 and `PipeReader` input, bounded resource limits, and backpressured output without first building a DOM.

> [!NOTE]
> The current development tree consumes unreleased AngleSharp work through a local source override. Package publishing
> is intentionally parked until that dependency has a clean upstream version. See
> [the upstream notes](docs/UPSTREAM_ANGLESHARP_NOTES.md).

## Read-only DOM

Use this when consumers benefit from normal node navigation but do not mutate the document.

```csharp
using AngleSharp.ReadOnlyDom;

var parser = ReadOnlyParser.CreateParser(ReadOnlyMetadataProfile.Minimal);
using var document = parser.ParseReadOnlyDocument(html);

var article = document.QueryOne(static node => node.TagId("article", "content"));
Console.WriteLine(article?.GetTextContent());
```

Metadata is explicit. `Minimal`, `Navigable`, `SourceMapped`, and `Diagnostic` profiles pay only for the capabilities
they expose.

## Compact DOM

Use the compact representation when a parsed document must survive several known queries.

```csharp
using AngleSharp.ReadOnlyDom.Compact;

var parser = CompactParser.CreateParser(CompactMetadataOptions.ParentLinks);
using var document = parser.ParseCompactDocument(html);

var article = document.Elements("article").WithAttribute("id", "content").First();
Console.WriteLine(article.Text());
```

Nodes and attributes live in pooled columns; lightweight handles provide the object-shaped view.

## Query UTF-8 directly

Use stream queries when the desired result is known before parsing and no DOM needs to escape.

```csharp
using AngleSharp.ReadOnlyDom.Streaming.Utf8Stream.Query;

var query = StreamQuery
    .For<List<string>>("article")
    .Descendant("h2")
    .OnNormalizedText(static (ref rows, in element) => rows.Add(element.GetText()))
    .Compile();

var headings = query.Execute(htmlUtf8, new List<string>());
```

Callbacks can consume borrowed UTF-8 spans or explicitly materialize owned strings. More complete examples cover
attributes, typed products, subtree text, arbitrary aggregate state, `PipeReader`, and backpressured output.
Independent query roots can be combined with `StreamQuery.Observe(...)`; `.Resolve(...)` turns their shared evidence
state into a caller-defined success, empty-result, provider-error, or unexpected-response outcome after EOF.

## Run it

The sample project walks through all representation lanes:

```powershell
dotnet run --project samples/AngleSharp.ReadOnlyDom.Samples -c Release
```

The Markdown proxy is an intentionally visible end-to-end demonstration of streaming HTML transformation:

```powershell
dotnet run --project samples/AngleSharp.ReadOnlyDom.MarkdownProxy -c Release
```

Run the test suite from the repository root:

```powershell
dotnet test AngleSharp.ReadOnlyDom.slnx -c Release
```

## Repository layout

```text
src/          library projects
tests/        correctness, compatibility, and html5lib coverage
benchmarks/   BenchmarkDotNet suites and corpus runners
samples/      runnable usage and integration examples
tools/        deterministic source generators
scripts/      repeatable benchmark entry points
docs/         architecture decisions, performance evidence, and upstream notes
```

Start with:

- [Samples](samples/AngleSharp.ReadOnlyDom.Samples/README.md)
- [Benchmark methodology and current results](docs/BENCHMARKING.md)
- [Compact DOM design](docs/COMPACT_DOM_DECISION.md)
- [Query-directed engine direction](docs/QUERY_DIRECTED_ENGINE_DIRECTION.md)
- [Metadata profiles](docs/METADATA_PROFILES.md)
- [Generated tag metadata](docs/TAG_METADATA.md)

## Performance work

Performance claims live beside their commands, fixtures, runtime settings, and captured artifacts in
[the benchmarking guide](docs/BENCHMARKING.md). The portable gates are elapsed time, throughput, total allocation, and
maximum buffered token size; retained size is treated as a secondary diagnostic.

```powershell
./scripts/bench.ps1 small
./scripts/bench.ps1 utf8-baseline
./scripts/bench.ps1 scraping
```

The goal is not a benchmark-only parser. It is a production-shaped HTML pipeline whose memory stays tied to selected
state and bounded tokens rather than input size.
