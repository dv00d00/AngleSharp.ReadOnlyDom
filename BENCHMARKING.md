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
dotnet run --project AngleSharp.ReadOnlyDom.Benchmarks -c Release -f net10.0 -- `
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

`utf8-tokenizer` is an exploratory wire-byte benchmark. It compares complete UTF-8-to-string decoding followed by the
AngleSharp tokenizer against the monotonic UTF-8 tokenizer kernel and a borrowed-span counting sink. Run
`dotnet run --project AngleSharp.ReadOnlyDom.Benchmarks -c Release -f net10.0 -- --utf8-token-smoke` first to write both
normalized token streams under `%TEMP%\AngleSharp.ReadOnlyDom\utf8-token-smoke` and fail at their first difference.

`utf8-rodom` measures complete compact RODOM construction plus a tag query from the same UTF-8 bytes. It compares a full
string decode, the resident byte text source, and the bounded AngleSharp streaming source with 4 KiB network-like reads.

`utf8-dom` compares decode plus mutable AngleSharp DOM construction and a `div` `{id, class, descendant text}` projection
against the native UTF-8 tokenizer folding directly into the same fingerprint. Setup requires exact equality with the
ordinary DOM on the 2 MB fixture. This validates that concrete view and corpus, not all HTML tree-construction behavior.

## Inventory

The project currently contains 15 BenchmarkDotNet classes and 59 benchmark methods. The curated script deliberately does
not run every historical or diagnostic probe under `all`; network-dependent and narrowly focused experiments should be
selected explicitly.

| Area | Benchmark | Curated tier | Question answered |
| --- | --- | --- | --- |
| Corpus | `CorpusBenchmark` | `small`, `full` | Standard DOM versus RODOM over checked-in pages |
| Compact construction | `CompactBuildBenchmark` | `compact` | RODOM versus frozen compact construction and parser reuse |
| Extraction | `CompactExtractionPlanBenchmark` | `extraction` | Traversal versus handwritten and interpreted compact plans |
| Extraction | `LongSyntheticConstructionBenchmark` | `extraction`, `scraping` | Same comparison on a 1.96 MB target-near-EOF document |
| Scraping | `QqArticleScraperBenchmark` | `extraction`, `scraping` | Exact `List<Article>` projection across DOM, compact, and native UTF-8 paths |
| UTF-8 | `Utf8TokenizerBenchmark` | `utf8` | Decode plus AngleSharp tokenization versus native UTF-8 tokenization |
| UTF-8 | `Utf8RodomBenchmark` | `utf8` | Decode, resident bytes, and bounded stream into compact RODOM |
| UTF-8 | `Utf8DomProjectionBenchmark` | `utf8` | Mutable DOM projection versus direct native UTF-8 fold |
| Query workloads | `QueryWorkloadRunner` | `query` | Repeated end-to-end query workload report with controlled iterations |
| Retained memory | `RetainedMemoryRunner` | `retained`, `full` | Forced-GC retained size and parse allocation |
| Query diagnostics | `CompactQueryWorkloadBenchmark`, `QueryBenchmark`, `SelectivityBenchmark` | direct filter | Isolated selector and selectivity mechanics |
| Construction diagnostics | `FatDocumentParsingBenchmark`, `FullFlowBenchmark` | direct filter | Large-page feature profiles and full object projection |
| Storage diagnostics | `OptionalNodeStorageLookupBenchmark` | direct filter | Dense versus sparse optional-node lookup |
| I/O diagnostics | `HttpParsingBenchmark` | direct filter | Live HTTP parsing; intentionally excluded from deterministic suites |

The remaining manual runners are `CollectionShapeRunner`, `CompactCorpusRunner`, and `CompactTraceRunner`. They are
diagnostic tools rather than throughput gates and are invoked through their dedicated `Program` switches.


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
