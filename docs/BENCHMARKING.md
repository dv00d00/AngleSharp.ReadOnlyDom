# Benchmarking

.NET 10 is the only performance runtime. Tests run on .NET 10 and .NET Framework 4.7.2; the library still builds for
.NET 8 and netstandard2.0, but those targets do not have dedicated benchmark or test executions.

All benchmark entry points require Server GC. The benchmark project enables it in its runtime configuration, the manual
runners fail immediately if it is unavailable, and the BenchmarkDotNet job explicitly requests it for generated hosts.

Run a tier from the repository root:

```powershell
./scripts/bench.ps1 small
./scripts/bench.ps1 compact
./scripts/bench.ps1 extraction
./scripts/bench.ps1 scraping
./scripts/bench.ps1 utf8
./scripts/bench.ps1 utf8-baseline
./scripts/bench.ps1 query
./scripts/bench.ps1 retained
./scripts/bench.ps1 full
./scripts/bench.ps1 all
```

`extraction` runs the extraction-plan, long synthetic construction-time extraction, and QQ object-projection
benchmarks. `scraping` is the shorter end-to-end subset containing only the QQ and long synthetic workloads. `utf8`
runs tokenizer, compact-RODOM construction, and mutable-DOM projection from wire bytes. The granular names (`plan`,
`long-streaming`, `utf8-tokenizer`, `utf8-rodom`, and `utf8-dom`) remain available when only one class is needed.

Run the anonymous three-document fat-page comparison directly:

```powershell
dotnet run --project benchmarks/AngleSharp.ReadOnlyDom.Benchmarks -c Release -f net10.0 -- `
  --filter "*FatDocumentParsingBenchmark*" --job short
```

It selects the three largest checked-in HTML fixtures as `LargeA`, `LargeB`, and `LargeC`, then compares default
AngleSharp, the production read-only DOM, and the frozen arena. Setup verifies equal element counts including template
contents and fails if an arena document falls back to packed layout.

Every run writes ignored, versioned output under `artifacts/benchmarks/<timestamp>-<commit>-<tier>/`, including commit,
runtime, job, corpus, and noise metadata. `small` parses five representative checked-in pages. `full` parses all
checked-in pages. `retained` runs the small
forced-GC retained-memory measurement without running BenchmarkDotNet.

`long-streaming` generates a deterministic long page with 5,000 attribute-heavy irrelevant sections and a small target
near EOF. It compares read-only DOM traversal, compact materialization plus plan, specialized AngleSharp construction-time
extraction, the EOF aggregate, raw native UTF-8 fold, and completed-element native UTF-8 fold. The generated fixture is
1.96 MB and puts the target near EOF, so every lane consumes the entire input; it does not claim early termination.

`utf8-baseline` freezes five equal 256 KiB tokenizer workloads: typical markup, malformed markup, raw-text/script data,
entity-heavy markup, and one pathological attribute token spanning almost the entire input. Every workload runs from both
contiguous memory and a 4 KiB segmented `PipeReader`; setup requires the two lanes to consume the same bytes and emit the
same chunk-insensitive token fingerprint. The tier also writes `utf8-baseline-diagnostics.md`, containing maximum buffered
token bytes and per-state byte visits, run counts, mean runs, and maximum runs. Reconsumed bytes count once for every state
that processes them, so diagnostic byte visits can exceed source length.

Use `./scripts/bench.ps1 utf8-baseline -HardwareCounters` to request BenchmarkDotNet's `TotalCycles` counter. Hardware
counters are deliberately opt-in because host support and stability vary. Each workload is exactly 262,144 bytes, so
multiply the reported `Op/s` by 0.25 for MiB/s and divide cycles per operation by 262,144 for cycles per byte. If
BenchmarkDotNet reports that the diagnoser is unavailable, retain time and throughput as the portable gate and record
cycles per byte as unavailable for that host.

The initial portable ShortRun capture on .NET 10.0.10 was written to
`artifacts/benchmarks/20260715-170602-6738aa4-utf8-baseline/`. Throughput below is `Op/s * 0.25 MiB`.

| Workload | Input | Mean | Throughput | Allocated | Maximum buffered token |
| --- | --- | ---: | ---: | ---: | ---: |
| Typical | Memory | 9.210 ms | 27.15 MiB/s | 1.18 KB | 16 B |
| Typical | PipeReader | 9.692 ms | 25.80 MiB/s | 8.88 KB | 16 B |
| Malformed | Memory | 10.185 ms | 24.55 MiB/s | 513.34 KB | 262,032 B |
| Malformed | PipeReader | 10.874 ms | 22.99 MiB/s | 521.03 KB | 262,032 B |
| Raw text | Memory | 11.793 ms | 21.20 MiB/s | 88.30 KB | 14 B |
| Raw text | PipeReader | 11.552 ms | 21.64 MiB/s | 96.00 KB | 14 B |
| Entity-heavy | Memory | 17.877 ms | 13.99 MiB/s | 1.18 KB | 22 B |
| Entity-heavy | PipeReader | 18.475 ms | 13.53 MiB/s | 8.88 KB | 22 B |
| Long token | Memory | 10.103 ms | 24.75 MiB/s | 513.19 KB | 262,128 B |
| Long token | PipeReader | 10.423 ms | 23.99 MiB/s | 520.88 KB | 262,128 B |

The final cumulative rerun after the accepted tokenizer and query-handoff work is under
`artifacts/benchmarks/20260715-190244-6738aa4-utf8-baseline/`:

| Workload | Input | Initial | Final | Change |
| --- | --- | ---: | ---: | ---: |
| Typical | Memory | 9.210 ms | 6.579 ms | 28.6% faster |
| Typical | PipeReader | 9.692 ms | 9.340 ms | 3.6% faster |
| Malformed | Memory | 10.185 ms | 4.357 ms | 57.2% faster |
| Malformed | PipeReader | 10.874 ms | 7.293 ms | 32.9% faster |
| Raw text | Memory | 11.793 ms | 5.726 ms | 51.4% faster |
| Raw text | PipeReader | 11.552 ms | 8.724 ms | 24.5% faster |
| Entity-heavy | Memory | 17.877 ms | 17.991 ms | 0.6% slower |
| Entity-heavy | PipeReader | 18.475 ms | 19.733 ms | 6.8% slower |
| Long token | Memory | 10.103 ms | 1.169 ms | 8.64x faster |
| Long token | PipeReader | 10.423 ms | 2.054 ms | 5.07x faster |

Allocation is unchanged across this tokenizer matrix. The entity-heavy regression is retained as an explicit tradeoff:
the accepted ordinary-span paths produce much larger wins on typical, malformed, raw-text, and long-token inputs, while
entities remain a scalar slow path. Relative to the original 1.96 MB read-only DOM extraction lane, the final native
completed-element fold moved from 39.09 ms / 11,474.97 KB to 14.93 ms / 3.07 KB: 2.62x faster with approximately 3,738x
less managed allocation.

The first Windows ETW `TotalCycles` capture resolved and emitted counters, but it was not stable enough to promote to a
gate: one malformed lane reported an impossible value and the other reported `NA`. Keep the opt-in counter lane for longer
confirmatory runs; do not infer cycles per byte from this ShortRun.

### Accepted tokenizer bulk scans

The initial three state-specific experiments were retained after isolated paired ShortRuns. Each reuses the existing
scalar state machine at semantic boundaries and leaves allocation unchanged.

| Experiment | Representative before | Confirmed after | Decision |
| --- | ---: | ---: | --- |
| Ordinary `ScriptData` spans, memory | 10.842 ms | 7.770 / 8.428 ms | Keep; 22–28% faster |
| Ordinary `ScriptData` spans, pipe | 11.552 ms frozen baseline | 8.057 / 8.027 ms | Keep; about 30% faster |
| Lowercase tag-name runs, memory | 9.230 ms | 6.906 / 6.908 ms | Keep; 25% faster |
| Lowercase tag-name runs, pipe | 9.567 ms | 9.078 / 9.122 ms | Keep; about 5% faster |
| Quoted values, Typical memory | 6.976–7.191 ms | 6.202 / 6.396 ms | Keep amended dispatch; 9–11% faster |
| Quoted values, Typical pipe | 9.018–9.062 ms | 9.109 / 8.947 ms | Neutral |
| Quoted values, LongToken memory | 10.383–10.757 ms | 1.219 / 1.162 ms | Keep; 88–89% faster |
| Quoted values, LongToken pipe | 10.833–10.871 ms | 2.039 / 1.972 ms | Keep; 81–82% faster |

The quoted-value experiment was amended after its first layout repeatedly cost about 2.5% on the Typical pipe lane. The
accepted `if / else if / else if` dispatch removes that extra branch from ordinary text execution while preserving the
large-value win. Artifacts are under `artifacts/benchmarks/experiment-script-bulk-*`,
`experiment-tagname-bulk-*`, and `experiment-quoted-attribute-bulk-*`.

A second mechanical pass attempted four more ideas. Rejected experiments were restored completely but their artifacts
remain useful evidence.

| Experiment | Representative before | Confirmed after | Decision |
| --- | ---: | ---: | --- |
| BCL vectorized text terminators, Typical memory | 6.235 ms | 6.332 / 6.493 ms | Reject; repeatable 1.6–4.1% regression |
| BCL vectorized text terminators, RawText memory | 6.042 ms | 5.822 / 5.696 ms | Reject; isolated win did not offset other lanes |
| Lowercase attribute names, Typical memory | 6.292 ms | 6.225 / 5.887 / 6.303 ms | Reject; wins did not repeat |
| Lowercase attribute names, Typical pipe | 9.102 ms | 8.777 / 8.966 / 9.530 ms | Reject; no stable win |
| Unquoted values, dedicated memory workload | 13.143 ms | 6.820 / 7.469 ms | Keep; 43–48% faster |
| Unquoted values, dedicated pipe workload | 14.373 ms | 9.598 / 9.820 ms | Keep; 32–33% faster |
| Comments, Malformed memory | 10.050 ms | 5.107 / 4.400 / 5.017 ms | Keep; 50–56% faster |
| Comments, Malformed pipe | 11.309 ms | 7.120 / 7.182 / 7.308 ms | Keep; 35–37% faster |

The temporary 256 KiB unquoted-value fixture used to establish signal was removed after measurement, leaving the frozen
benchmark inventory unchanged. Typical lanes stayed within the 1% regression gate for both retained changes. Artifacts
are under `experiment-vector-text-scan-*`, `experiment-attribute-name-bulk-*`,
`experiment-unquoted-value-bulk-*`, and `experiment-comment-bulk-*`.

### Query-directed tokenizer handoff

The compiled query path now reuses the tag hash accumulated while tokenizing, dispatches a hash/length pair directly to
the query-node candidate bitset, and asks the query session whether each attribute value is required before copying it.
Ordinary token sinks retain the original callback contract and still receive every attribute.

A temporary cumulative 1.4 MB / five-node diagnostic fixture compared the old linear path with each optimization. Two
ShortRuns were sensitive to Tiered PGO and code layout, but the complete path improved from 9.353 ms to 9.198 ms in the
first run and from 9.151 ms to 8.717 ms in the repeat. Managed allocation was unchanged; the apparent 32-byte difference
was the legacy benchmark adapter. The attribute-heavy correctness fixture also bounds an ignored 8 KiB attribute below
256 bytes of tokenizer-owned buffering.

Real-workload confirmation on .NET 10 Server GC:

| Workload | Result | Allocated |
| --- | ---: | ---: |
| 1.96 MB synthetic, native raw fold | 14.80 ms | 8.03 KB |
| 1.96 MB synthetic, completed-element fold | 14.93 ms | 3.07 KB |
| 125 KB QQ scraper, compiled callbacks | 930.0 us | 15.76 KB |
| 125 KB QQ scraper, completed elements | 923.9 us | 15.68 KB |

These are confirmation numbers for the cumulative tokenizer state, not isolated attribution: the synthetic workload's
earlier documented 18 ms checkpoint predates the accepted bulk-scanning changes above.

The .NET 10 ShortRun measured while introducing the native folds was:

| Method | Mean | Allocated |
| --- | ---: | ---: |
| Read-only parse and traverse | 39.09 ms | 11,474.97 KB |
| Compact parse and plan | 42.01 ms | 6,263.66 KB |
| Query-directed construction | 33.98 ms | 1,456.11 KB |
| EOF aggregate construction | 32.40 ms | 1,456.23 KB |
| Native UTF-8 raw fold | 17.67 ms | 8.00 KB |
| Native UTF-8 completed-element fold | 17.01 ms | 5.49 KB |

ShortRun timing differences between the two native folds are noise-sized. Their allocation difference is structural: the
raw benchmark normalizes a captured string after parsing, while the completed-element handler normalizes as bytes arrive.

After completed-element captures moved to reusable pooled UTF-8 buffers, an isolated rerun measured 18.01 ms / 8.01 KB
for the raw fold and 18.60 ms / 3.05 KB for the completed-element fold. The returned normalized string is included in both
allocation totals; UTF-8-only aggregate callbacks can avoid that final ownership cost as well.

`utf8-tokenizer` is an exploratory wire-byte benchmark. It compares complete UTF-8-to-string decoding followed by the
AngleSharp tokenizer against the monotonic UTF-8 tokenizer kernel and a borrowed-span counting sink. Run
`dotnet run --project benchmarks/AngleSharp.ReadOnlyDom.Benchmarks -c Release -f net10.0 -- --utf8-token-smoke` first to write both
normalized token streams under `%TEMP%\AngleSharp.ReadOnlyDom\utf8-token-smoke` and fail at their first difference.

`utf8-rodom` measures complete compact RODOM construction plus a tag query from the same UTF-8 bytes. It compares a full
string decode, the resident byte text source, and the bounded AngleSharp streaming source with 4 KiB network-like reads.

`utf8-dom` compares decode plus mutable AngleSharp DOM construction and a `div` `{id, class, descendant text}` projection
against the native UTF-8 tokenizer folding directly into the same fingerprint. Setup requires exact equality with the
ordinary DOM on the 2 MB fixture. This validates that concrete view and corpus, not all HTML tree-construction behavior.

## Real HttpClient streaming fold

`HttpClientStreamingQueryBenchmark` serves deterministic HTML from a loopback TCP HTTP/1.1 server. Every operation uses
a real `HttpClient` request and socket; it compares `GetByteArrayAsync` plus a fold against
`ResponseHeadersRead -> response stream -> PipeReader -> query`. This keeps DNS, TLS, CDN state, and public-network jitter
out of the parser measurement.

```powershell
dotnet run --project benchmarks/AngleSharp.ReadOnlyDom.Benchmarks -c Release -f net10.0 -- `
  --filter "*HttpClientStreamingQueryBenchmark*"
```

ShortRun, Server GC, .NET 10.0.10:

| HTML | Lane | Mean | Allocated |
| ---: | --- | ---: | ---: |
| 128 KB | Materialize then fold | 999.7 us | 147.14 KB |
| 128 KB | Response stream fold | 897.5 us | 19.80 KB |
| 2 MB | Materialize then fold | 8.225 ms | 2,067.58 KB |
| 2 MB | Response stream fold | 6.368 ms | 19.92 KB |

The 2 MB response-stream lane was about 22% faster and used about 99% less managed memory. The generated report is
`BenchmarkDotNet.Artifacts/results/AngleSharp.ReadOnlyDom.Benchmarks.HttpClientStreamingQueryBenchmark-report-github.md`.

## Inventory

The project currently contains 18 BenchmarkDotNet classes and 80 benchmark methods. The curated script deliberately does
not run every historical or diagnostic probe under `all`; network-dependent and narrowly focused experiments should be
selected explicitly.

| Area | Benchmark | Curated tier | Question answered |
| --- | --- | --- | --- |
| Corpus | `CorpusBenchmark` | `small`, `full` | Standard DOM versus RODOM over checked-in pages |
| Compact construction | `CompactBuildBenchmark` | `compact` | RODOM versus frozen compact construction and parser reuse |
| Extraction | `CompactExtractionPlanBenchmark` | `extraction` | Traversal versus handwritten and interpreted compact plans |
| Extraction | `LongSyntheticConstructionBenchmark` | `extraction`, `scraping` | Same comparison on a 1.96 MB target-near-EOF document |
| Scraping | `QqArticleScraperBenchmark` | `extraction`, `scraping` | Exact `List<Article>` projection across AngleSharp input/query baselines, RODOM, compact, and native UTF-8 paths |
| UTF-8 | `Utf8TokenizerBenchmark` | `utf8` | Decode plus AngleSharp tokenization versus native UTF-8 tokenization |
| UTF-8 | `Utf8TokenizerBaselineBenchmark` | `utf8-baseline` | Frozen state/pathology matrix over memory and segmented pipe input |
| UTF-8 | `Utf8RodomBenchmark` | `utf8` | Decode, resident bytes, and bounded stream into compact RODOM |
| UTF-8 | `Utf8DomProjectionBenchmark` | `utf8` | Mutable DOM projection versus direct native UTF-8 fold |
| Query workloads | `QueryWorkloadRunner` | `query` | Repeated end-to-end query workload report with controlled iterations |
| Retained memory | `RetainedMemoryRunner` | `retained`, `full` | Forced-GC retained size and parse allocation |
| Query diagnostics | `CompactQueryWorkloadBenchmark`, `QueryBenchmark`, `SelectivityBenchmark` | direct filter | Isolated selector and selectivity mechanics |
| Construction diagnostics | `FatDocumentParsingBenchmark`, `FullFlowBenchmark` | direct filter | Large-page feature profiles and full object projection |
| Storage diagnostics | `OptionalNodeStorageLookupBenchmark` | direct filter | Dense versus sparse optional-node lookup |
| I/O diagnostics | `HttpClientStreamingQueryBenchmark` | direct filter | Deterministic real-socket materialization versus response-stream fold |
| I/O diagnostics | `HttpParsingBenchmark` | direct filter | Live HTTP parsing; intentionally excluded from deterministic suites |

The remaining manual runners are `CollectionShapeRunner`, `CompactCorpusRunner`, and `CompactTraceRunner`. They are
diagnostic tools rather than throughput gates and are invoked through their dedicated `Program` switches.

### AngleSharp scraper baselines

`QqArticleScraperBenchmark` exposes two named file parameters. `qq.html` is the checked-in 124,794-byte page and produces
16 owned `Article` objects. `qq-x4.html` retains one document head and repeats the body workload four times, producing a
492,711-byte page and 64 articles. This keeps selector shape and result density constant. Every lane must return the same
objects for each parameter. The mutable AngleSharp matrix covers the useful axes without generating every redundant
cross-product:

- string input with the default parser, queried through CSS selectors, native DOM collection APIs, and a manual tree walk;
- string input with scraper-specific parser options, queried through those APIs plus precompiled CSS selectors;
- UTF-16 memory plus auto-detected and explicit UTF-8 memory sources, queried through CSS selectors;
- legacy synchronous, buffered asynchronous, and bounded-streaming `MemoryStream` input queried through CSS selectors.

The remaining methods compare those baselines with the read-only DOM, filtered/source-mapped DOM, frozen compact DOM,
handwritten native UTF-8 fold, compiled callback query, and completed-element query. `GlobalSetup` fails on the first
result mismatch, so a faster method cannot silently benchmark a weaker projection.

Run the complete 40-case matrix with:

```powershell
dotnet run --project benchmarks/AngleSharp.ReadOnlyDom.Benchmarks -c Release -f net10.0 -- `
  --filter "*QqArticleScraperBenchmark*" --join
```

The final .NET 10 ShortRun on an i9-9900K produced the following `qq.html` AngleSharp baselines:

| AngleSharp lane | Mean | Allocated |
| --- | ---: | ---: |
| Default parser + CSS | 3.284 ms | 1,248.91 KB |
| Default parser + DOM collections | 3.244 ms | 1,458.87 KB |
| Default parser + manual tree walk | 3.291 ms | 1,317.57 KB |
| Scraper options + CSS | 2.526 ms | 938.00 KB |
| Scraper options + precompiled CSS | 2.486 ms | 925.55 KB |
| Scraper options + DOM collections | 2.678 ms | 1,147.95 KB |
| Scraper options + manual tree walk | 2.553 ms | 1,006.66 KB |
| UTF-16 memory + CSS | 2.530 ms | 938.00 KB |
| Auto-detected UTF-8 memory + CSS | 2.473 ms | 938.02 KB |
| Explicit UTF-8 memory + CSS | 2.562 ms | 937.99 KB |
| Legacy synchronous stream + CSS | 3.002 ms | 1,714.95 KB |
| Buffered asynchronous stream + CSS | 3.066 ms | 1,463.05 KB |
| Bounded streaming source + CSS | 2.637 ms | 982.50 KB |

The main baseline lesson is parser configuration rather than traversal cleverness: dropping unneeded parser features and
attributes saved roughly 23% time and 25% allocation. Precompiling selectors saved another 12.5 KB; collection and
manual-walk variants did not produce a repeatable time win on this fixture.

The x4 parameter shows near-linear CPU scaling across every lane, but a different allocation slope for direct queries:

| Representative lane | 1x mean | x4 mean | Time growth | 1x allocated | x4 allocated | Allocation growth |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Default AngleSharp | 3.284 ms | 12.792 ms | 3.90x | 1,248.91 KB | 4,881.94 KB | 3.91x |
| Configured precompiled CSS | 2.486 ms | 9.912 ms | 3.99x | 925.55 KB | 3,633.07 KB | 3.93x |
| Legacy AngleSharp stream | 3.002 ms | 11.992 ms | 4.00x | 1,714.95 KB | 6,716.05 KB | 3.92x |
| Bounded AngleSharp stream | 2.637 ms | 10.582 ms | 4.01x | 982.50 KB | 3,833.84 KB | 3.90x |
| Minimal RODOM | 1.565 ms | 6.252 ms | 4.00x | 227.73 KB | 894.13 KB | 3.93x |
| Compact DOM | 1.697 ms | 6.834 ms | 4.03x | 93.95 KB | 358.87 KB | 3.82x |
| Compiled UTF-8 query | 866.9 us | 3.530 ms | 4.07x | 15.80 KB | 51.59 KB | 3.27x |
| Completed-element query | 938.0 us | 3.676 ms | 3.92x | 15.72 KB | 51.30 KB | 3.26x |

BenchmarkDotNet's `Allocated` column is total managed allocation, not peak process working set. The direct-query result is
still informative: input and owned output both grow fourfold while allocation grows only 3.26x, from about 16 KB to 51 KB.
The parser stack and buffers therefore do not contribute an input-sized allocation; most growth is the additional 48
returned `Article` objects and their owned strings. DOM construction remains input-sized.


## Retained-memory method

Corpus strings are loaded and rooted before measurement. A forced full collection establishes the managed-heap baseline.
Every completed document stays reachable until another forced full collection measures retained bytes. Documents are then
disposed and released before the next implementation. Three runs are performed and the median of each metric is reported.

`GC.GetAllocatedBytesForCurrentThread` records total parse allocation. Approximate peak live heap is sampled after every
document; it is not a high-frequency peak measurement. Run on an idle machine with the same SDK, commit, power plan, and
corpus. Compare retained bytes, not process working set.

The report includes element, text-node, and attribute counts plus retained bytes per node and per attribute. These ratios
are descriptive rather than additive: a document's retained bytes include collections, strings, source buffers, and other
shared state.

## Regression gate

- Micro allocation: investigate any repeatable increase above 1% or any unexpected Gen1/Gen2 change.
- Corpus allocation or retained bytes: investigate a repeatable increase above 3%.
- Time: rerun before acting; treat a repeatable increase above 5% outside overlapping noise as material.
- Correctness remains mandatory regardless of performance.

For a previous read-only implementation, run the same command in a clean worktree at the baseline commit and compare the
versioned reports. Standard AngleSharp is the BenchmarkDotNet baseline in every throughput tier and is included in the
retained-memory report.

## Established .NET 10 baseline

The initial ShortRun and three-run retained medians on the benchmark-development tree based on `847ba33` were:

| Tier / implementation | Mean | Allocated | Retained |
| --- | ---: | ---: | ---: |
| Micro full / read-only | 4.912 ms | 706.55 KB | — |
| Micro filtered / read-only | 3.989 ms | 212.11 KB | — |
| Small corpus / standard | 1,084.3 ms | 232.13 MB | 215.15 MB |
| Small corpus / read-only | 531.9 ms | 65.69 MB | 62.17 MB |
| Full corpus / standard | 1,309.7 ms | 320.14 MB | 268.69 MB |
| Full corpus / read-only | 646.3 ms | 77.71 MB | 72.01 MB |

ShortRun confidence intervals are wide, especially for corpus timing; allocation and repeated retained bytes are the
primary regression signals.
