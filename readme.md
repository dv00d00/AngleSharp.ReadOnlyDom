# AngleSharp.ReadOnlyDom

Parse HTML without paying for a fully mutable DOM when the result is only going to be read, queried, or folded into
another shape.

This repository explores a spectrum of representations built around AngleSharp's HTML semantics:

| Lane | Retained state | Best fit |
| --- | --- | --- |
| Read-only DOM | Familiar object graph | General navigation with lower allocation than the mutable DOM |
| Compact DOM | Pooled columnar document | Repeated queries and longer-lived parsed documents |
| UTF-8 stream query | Bounded parser/query state | Extracting typed rows, text, JSON, or Markdown directly from wire bytes |
| UTF-8 stream rewrite | Bounded holdback | Editing elements and text in flight while untouched bytes are forwarded verbatim |

The streaming lane is the main experimental direction: selectors are compiled into the tokenizer's target encoding,
only requested attributes and text are captured, and caller-owned state determines the result shape. It supports
contiguous UTF-8 and `PipeReader` input, bounded resource limits, and backpressured output without first building a DOM.

Extracting `a[href]` counts across a 47-document corpus allocates **888 B to 2 KB per parse regardless of document
size** — a 1.5 MB page allocates less than a 434 KB one — and throughput edges ahead of a PGO-tuned build of
[`lol-html`](https://github.com/cloudflare/lol-html), the Rust engine behind Cloudflare Workers' `HTMLRewriter`:
**median +9.5% over the corpus, +6.8% over the documents large enough to be tokenizer-bound**, with the remaining
losses confined to pages dominated by large inline `<script>`/`<style>` bodies. See
[Performance](#performance) for the per-document numbers and the method.

> [!NOTE]
> The streaming-query assembly and Markdown proxy are self-contained and do not reference AngleSharp at runtime. The
> object and compact DOM projects still consume unreleased AngleSharp construction work through a local source override.
> Publishing those DOM packages is parked until that dependency has a clean upstream version. See
> [the upstream notes](docs/UPSTREAM_ANGLESHARP_NOTES.md).

## Build the DOM projects against the AngleSharp fork

`AngleSharp.ReadOnlyDom.Streaming` and its Markdown proxy build directly with the .NET SDK. The object and compact DOM
projects currently require the matching AngleSharp fork branch. On a fresh Windows machine, install the .NET 10 SDK and
clone both repositories into the same parent directory:

```powershell
$workspace = 'C:\src\anglesharp-work'
New-Item -ItemType Directory -Force $workspace | Out-Null

git clone --branch devel https://github.com/dv00d00/AngleSharp.git "$workspace\AngleSharp"
git clone --branch main https://github.com/dv00d00/AngleSharp.ReadOnlyDom.git "$workspace\AngleSharp.ReadOnlyDom"

Set-Location "$workspace\AngleSharp.ReadOnlyDom"
```

The tracked targets file replaces explicit AngleSharp package references with the fork's `AngleSharp.Core.csproj`; it
does not inject the fork into the standalone streaming or Markdown projects. Sibling clones named `AngleSharp` and
`AngleSharp.ReadOnlyDom` are detected automatically. For any other layout, set the source root before restoring:

```powershell
$env:AngleSharpSourceRoot = (Resolve-Path 'D:\src\AngleSharp').Path
```

Restore after enabling or changing the source override so every target framework gets fresh project assets. Build
serially because the solution and the fork share AngleSharp output paths:

```powershell
dotnet restore AngleSharp.ReadOnlyDom.slnx --force --no-cache
dotnet build AngleSharp.ReadOnlyDom.slnx -c Release --no-restore -m:1
dotnet test tests/AngleSharp.ReadOnlyDom.Tests/AngleSharp.ReadOnlyDom.Tests.csproj -c Release -f net10.0 --no-restore
```

The Release build output should contain an
`AngleSharp.Core -> ...\AngleSharp\src\AngleSharp\bin\Release\...\AngleSharp.dll` line.
If it does not, the build is still consuming the NuGet package.

For Rider, make `AngleSharpSourceRoot` a persistent user variable and restart Rider before opening the solution:

```powershell
[Environment]::SetEnvironmentVariable(
    'AngleSharpSourceRoot',
    (Resolve-Path 'D:\src\AngleSharp').Path,
    'User'
)
```

`Directory.Build.targets` is part of the repository so CI, temporary worktrees, and fresh clones all use the same
override logic. Keep machine-specific paths out of it and set `AngleSharpSourceRoot` in the environment instead.

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
using AngleSharp.ReadOnlyDom.Compact.Document;
using AngleSharp.ReadOnlyDom.Compact.Parsing;
using AngleSharp.ReadOnlyDom.Compact.Query;

var parser = CompactParser.CreateParser(CompactMetadataOptions.ParentLinks);
using var document = parser.ParseCompactDocument(html);

var article = document.Elements("article").WithAttribute("id", "content").First();
Console.WriteLine(article.Text());
```

Nodes and attributes live in pooled columns; lightweight handles provide the object-shaped view.

## Query UTF-8 directly

Use stream queries when the desired result is known before parsing and no DOM needs to escape.

```csharp
using AngleSharp.ReadOnlyDom.Streaming.Query;

var query = StreamQuery
    .For<List<string>>("article")
    .Descendant("h2")
    .OnNormalizedText(static (ref rows, in element) => rows.Add(element.GetText()))
    .Compile();

var headings = query.Execute(htmlUtf8, new List<string>());
```

`Child` and `Descendant` follow the lexical start/end-tag stack, not browser-corrected HTML tree topology; use a retained
DOM lane when implied end tags, foster parenting, or other tree-construction recovery must affect relationships.

Callbacks can consume borrowed UTF-8 spans or explicitly materialize owned strings. More complete examples cover
attributes, typed products, subtree text, arbitrary aggregate state, and an end-to-end `PipeReader` to backpressured
NDJSON `PipeWriter` content-feed transformation with no intermediate DOM or row list.
Independent query roots can be combined with `StreamQuery.Observe(...)`; after execution, caller-owned evidence can be
resolved into success, empty-result, provider-error, or unexpected-response outcomes.

## Rewrite HTML as a stream

The same compiled selectors can mutate matched elements without constructing a DOM. Attribute edits, insertions around
or inside an element, inner-content replacement, whole-element replacement/removal, and tag unwrapping are applied while
untouched input is forwarded byte-for-byte. Removed descendants are discarded as they arrive rather than buffered until
the closing tag.

```csharp
using System.Buffers;
using AngleSharp.ReadOnlyDom.Streaming.Query;
using AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;

var links = StreamQuery.For<int>("a").Attribute("href").Compile();
var output = new ArrayBufferWriter<byte>();

links.Rewrite(
    htmlUtf8,
    output,
    0,
    static (ref int count, in Element link, ref ElementRewriter element) =>
    {
        count++;
        element.SetAttribute("rel"u8, "noopener noreferrer"u8);
        element.Prepend("<span class=\"sr-only\">Story: </span>"u8, HtmlRewriteContentType.Html);
        element.After("<!-- rewritten -->"u8, HtmlRewriteContentType.Html);
    }
);
```

`CreateRewriteSession` exposes the same operations for chunked input and a backpressured `IBufferWriter<byte>`. Content
marked as `Text` is escaped; `Html` is trusted and emitted verbatim. Element matching and closure follow the lexical tag
stack, so use a tree-building lane when browser-corrected topology is part of the rewrite policy.

### Rewriting text

Element and text handlers compose in a single pass. A text handler receives borrowed, undecoded UTF-8 fragments along
with their tokenizer context — `Data`, `RcData`, `RawText`, `ScriptData`, `PlainText`, `CDataSection` — and a flag
marking the last fragment of a text node, so large text nodes keep streaming instead of being buffered to be seen whole.

```csharp
var article = StreamQuery.For<int>("article").Compile();
var output = new ArrayBufferWriter<byte>();

article.Rewrite(
    htmlUtf8,
    output,
    0,
    new HtmlRewriteHandlers<int>(
        text: static (ref int redacted, in TextChunk chunk, ref TextChunkRewriter text) =>
        {
            if (chunk.IsLastInTextNode)
                return;
            redacted += chunk.Utf8.Length;
            text.Replace("[redacted]"u8, HtmlRewriteContentType.Text);
        }
    )
);
```

`Before`, `After`, `Replace`, and `Remove` are available per fragment, with the same `Text`/`Html` payload contract as
element mutations. Text below a removed, replaced, or inner-replaced element is skipped rather than reported and
discarded, and mutation state is recycled inside the session so a redaction pass does not allocate per matched chunk.

## Performance

The reference point is [`lol-html`](https://github.com/cloudflare/lol-html) 3.0.1, compiled for comparison with fat LTO,
a single codegen unit, `target-cpu=native`, and LLVM PGO — an untuned Rust build is not an honest bar. Cross-engine runs
interleave the two lanes **ABBA** within each round so that drift in machine state cancels instead of accruing to
whichever lane happened to run second. Numbers below come from one Zen 3 machine: per document, two independent passes
of five interleaved 3-second rounds per lane, reported as the median of all ten samples and cross-checked pass against
pass. Documents whose two engines disagree on the extracted values are shown but excluded from every aggregate.

### Extraction, against tuned lol-html

Counting `a[href]`, 4 KiB chunked push, workstation GC, over the whole 47-document corpus. Documents per second;
each figure is the median of ten samples — two independent passes of five ABBA-interleaved rounds each.

Over the 44 documents where both engines agree on the extracted values: **31 wins, 13 losses, median +9.5%**, range
−31.2% to +145.7%. The spread is far wider than the median suggests, so the full per-document result is published
rather than a chosen subset.

Two cuts of the same data matter more than the headline:

- **Documents ≥ 100 KB** (23 of them, where throughput is genuinely tokenizer-bound): 14 wins, 9 losses,
  **median +6.8%**. This is the honest structural number.
- **Documents < 100 KB** (21): 17 wins, 4 losses, **median +13.7%**, and every extreme in the set. `godaddy` is 273
  bytes and `weibo`, `msn`, `tmall`, `pcmag` match zero anchors; at 200k–1.4M documents per second those rows price
  per-parse fixed cost and process-loop overhead, not scanning. `godaddy` +146% is a setup-cost result and should not
  be read as a parsing win.

The losses are one shape, not scattered noise: every document that still loses is dominated by large inline
`<script>`/`<style>` bodies whose bytes both engines merely skip — `news.google` (−31%, 77% of its bytes sit in one
318 KB style element), `amazon`, `tmall`, `msn`, `nbc`, `yahoo`, `aliexpress`, `baidu`. Per-byte skip rate over
discarded raw text is the open attribution question; markup-dense documents, which were the losing family a release
ago, now win (`linkedin` moved from −15.1% to −2.7%, `stackoverflow` from +19.3% to +31.3%).

| Corpus | KB | `a[href]` | tuned lol-html | this library | Δ % |
| --- | ---: | ---: | ---: | ---: | ---: |
| w3 | 13,265 | 59,914 | 21 | 31 | +43.5 |
| spiegel | 2,052 | 679 | 662 | 803 | +21.3 |
| yahoo | 1,493 | 58 | 6,691 | 6,009 | −10.2 |
| huffingtonpost | 1,171 | 354 | 1,700 | 1,726 | +1.6 |
| nbc | 1,161 | 459 | 1,790 | 1,617 | −9.6 |
| imdb | 981 | 149 | 2,369 | 3,064 | +29.4 |
| nytimes | 693 | 622 | 2,114 | 2,258 | +6.8 |
| en.wikipedia | 681 | 1,280 | 1,101 | 1,197 | +8.7 |
| flickr | 646 | 68 | 5,380 | 8,912 | +65.7 |
| 163 | 600 | 1,375 | 1,419 | 1,553 | +9.5 |
| reddit | 595 | 167 | 2,605 | 2,920 | +12.1 |
| ebay † | 587 | 374 | 2,758 | 2,926 | +6.1 |
| news.google | 460 | 8 | 12,033 | 8,278 | −31.2 |
| baidu | 434 | 53 | 20,537 | 19,147 | −6.8 |
| mail.ru | 394 | 171 | 7,273 | 6,975 | −4.1 |
| aliexpress | 294 | 105 | 16,013 | 14,786 | −7.7 |
| pinterest † | 294 | 1 | 58,961 | 40,006 | −32.1 |
| google | 265 | 11 | 56,936 | 58,252 | +2.3 |
| myspace | 240 | 257 | 3,385 | 3,166 | −6.5 |
| sitepoint | 192 | 149 | 7,749 | 9,917 | +28.0 |
| stackoverflow | 176 | 118 | 5,771 | 7,579 | +31.3 |
| linkedin | 135 | 153 | 8,706 | 8,468 | −2.7 |
| wordpress | 134 | 73 | 11,967 | 11,486 | −4.0 |
| qq | 122 | 288 | 5,409 | 6,034 | +11.6 |
| bing | 119 | 42 | 21,172 | 23,535 | +11.2 |
| codeproject † | 115 | 123 | 12,850 | 13,605 | +5.9 |
| ask | 93 | 74 | 13,009 | 13,568 | +4.3 |
| 360.cn | 91 | 480 | 4,114 | 4,832 | +17.5 |
| html5rocks | 87 | 70 | 10,938 | 11,274 | +3.1 |
| netflix | 87 | 19 | 56,342 | 56,680 | +0.6 |
| tumblr | 51 | 20 | 31,041 | 34,659 | +11.7 |
| msn | 42 | 0 | 195,536 | 160,382 | −18.0 |
| peacekeeper.futuremark | 27 | 103 | 18,972 | 18,873 | −0.5 |
| taobao | 20 | 108 | 22,664 | 29,811 | +31.5 |
| tmall | 19 | 0 | 262,521 | 203,932 | −22.3 |
| html5test | 19 | 13 | 49,813 | 58,419 | +17.3 |
| kickass.to | 18 | 1 | 120,918 | 144,254 | +19.3 |
| florian-rappl | 11 | 58 | 32,536 | 32,973 | +1.3 |
| weibo | 9 | 0 | 448,168 | 685,740 | +53.0 |
| amazon | 7 | 2 | 146,969 | 110,550 | −24.8 |
| youtube | 6 | 13 | 115,563 | 134,778 | +16.6 |
| neobux | 5 | 4 | 157,724 | 179,303 | +13.7 |
| pcmag | 5 | 0 | 379,142 | 439,218 | +15.8 |
| blogspot | 4 | 7 | 168,648 | 207,351 | +22.9 |
| vk | 3 | 5 | 206,510 | 226,006 | +9.4 |
| live | 1 | 1 | 373,113 | 521,630 | +39.8 |
| godaddy | 0 | 0 | 558,717 | 1,372,735 | +145.7 |

† Excluded from the win/loss counts and medians: the two engines extract different anchor sets. Each of these three
documents contains exactly one `<a href>` inside a `<noscript>` element, which this engine finds and lol-html does not.
`<noscript>` is raw text only when scripting is enabled; lol-html hardcodes scripting-enabled parsing, while this engine
follows the scripting-disabled default that AngleSharp uses and that suits server-side extraction. The divergence only
ever runs one way — no document has this engine missing an anchor lol-html reports.

Both passes agreed closely: the per-document Δ moved by a median of 1.0 percentage point between them, at most 7.5
(`godaddy`, a 273-byte document), and only `huffingtonpost` and `netflix` changed sign — both within a few percent of
parity either way.

### Allocation

Whole-corpus BenchmarkDotNet sweep, 47 documents, server GC:

| Document | Size | Allocated per parse |
| --- | ---: | ---: |
| weibo | 9 KB | 888 B |
| msn | 42 KB | 888 B |
| aliexpress | 294 KB | 1,449 B |
| baidu | 434 KB | 2,008 B |
| imdb | 981 KB | 1,171 B |
| yahoo | 1,493 KB | 1,168 B |

Allocation is bounded by the query's retained state, not by input length, and gen-0 collections are absent on most
documents. Feeding the same input as 4 KiB chunks instead of one buffer costs a median 5.8% (range 1.2%–12%).

Throughput per byte varies by more than an order of magnitude across the corpus — small cache-resident documents scan far
faster than a 13 MB one — so this repository reports per-corpus figures rather than a single header number.

### Against a DOM-based .NET baseline

Extracting plain text with an HtmlAgilityPack implementation versus the streaming lane, over synthetic clinician notes,
Outlook reply chains, and Word "Save as Web Page" letters (968 B – 44 KB): **6.8× to 9.0× faster, allocating 31× to 118×
less**. The DOM-building lanes in this repository are not the fast path and make no such claim.

### What is not claimed

These are single-machine numbers over a fixed corpus with one selector shape. Other documents, selectors, and hardware
will move them. The method is published so the claims can be rechecked, and refuted hypotheses are recorded in the issue
history beside the wins. The table above is reproduced by:

```powershell
./scripts/bench-cross-engine-corpus.ps1
```

## Run it

The sample project walks through all representation lanes:

```powershell
dotnet run --project samples/AngleSharp.ReadOnlyDom.Samples -c Release
```

The Markdown proxy is an intentionally visible end-to-end demonstration of streaming HTML transformation:

```powershell
dotnet run --project samples/AngleSharp.ReadOnlyDom.MarkdownProxy -c Release
```

Opinionated text/Markdown projections and safe local Markdown navigation remain runnable examples rather than library
surface:

```powershell
dotnet run --project samples/AngleSharp.ReadOnlyDom.ExtractionExamples -c Release
dotnet run --project samples/AngleSharp.ReadOnlyDom.MarkdownNavigation -c Release
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
- [Text and Markdown extraction examples](samples/AngleSharp.ReadOnlyDom.ExtractionExamples/README.md)
- [Streaming HTML-to-Markdown navigation](samples/AngleSharp.ReadOnlyDom.MarkdownNavigation/README.md)
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
