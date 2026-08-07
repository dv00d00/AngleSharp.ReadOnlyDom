# Benchmarking

.NET 10 with Server GC is the canonical performance environment. The benchmark executable rejects workstation GC,
and the curated script records the commit, working-tree state, runtime, tier, and corpus beside every result.

Run a maintained tier from the repository root:

```powershell
./scripts/bench.ps1 small
./scripts/bench.ps1 full
./scripts/bench.ps1 compact
./scripts/bench.ps1 compact-stages
./scripts/bench.ps1 extraction
./scripts/bench.ps1 rows
./scripts/bench.ps1 scraping
./scripts/bench.ps1 query
./scripts/bench.ps1 utf8
./scripts/bench.ps1 utf8-baseline
./scripts/bench.ps1 retained
./scripts/bench.ps1 all
./scripts/bench-native-console.ps1
./scripts/bench-product.ps1
```

Use `-HardwareCounters` only for a longer confirmatory run. Counter availability is host-dependent; portable time,
throughput, and allocation remain the default gate.

## Native product comparison

The primary engine comparison uses two standalone console applications, with no P/Invoke and no HTTP stack in the
timed region. The Rust release binary and .NET Release JIT process load the same UTF-8 corpus before timing, create a new
rewriter/query execution per document, feed it in exact 4 KiB chunks, own every selected URL, and consume an identical
checksum. The default run alternates process order over five 10-second rounds:

```powershell
./scripts/bench-native-console.ps1
```

`-Workload passthrough` keeps an identical never-matching `zz` selector on both sides, forcing both engines to parse
tags while leaving the document unchanged. `-Workload match` runs the product selector and counts matches without
materializing attribute values. The default `-Workload extract` owns the selected UTF-8 URLs and checks their content.
The handler-free `lol_html` raw-forwarding fast path is intentionally not used for the comparable passthrough lane.

Use `-NativeAot` for the secondary ahead-of-time comparison. It publishes with the standard
`OptimizationPreference=Speed`; keep JIT as the default for sustained production-server throughput.

For an end-to-end service comparison, `bench-product.ps1` compares two independent long-lived HTTP services without
interop: a normal Rust release binary using `lol_html` and a .NET NativeAOT/Kestrel binary using the streaming query
engine.

The load runner sends HTTP/1.1
keep-alive POST requests whose UTF-8 bodies are transferred without a content length and written in 4 KiB chunks. Both
services consume the request as a stream, execute the same selector, own the selected URLs, and return the same
newline-delimited UTF-8 response. A correctness request runs before timing.

The default run uses three alternating 10-second rounds at concurrency 1 and 6 over the checked-in QQ page and its 4x
body variant. Alternating service order reduces temperature and frequency bias. The report includes throughput, p50,
p95, p99, process CPU per request, and peak process working set. This is an end-to-end service comparison: HTTP stack,
request streaming, parser/query, result ownership, and response writing are intentionally included.

```powershell
./scripts/bench-product.ps1 -Seconds 15 -Rounds 5 -Concurrency "1,6"
./scripts/bench-product.ps1 -Endpoint rewrite-full -Seconds 15 -Rounds 5 -Concurrency "1,6"
```

The `rewrite` endpoint adds one attribute to each matched anchor. `rewrite-full` runs the same selector and performs the
same multi-operation element rewrite in both engines: set and remove attributes, insert content before/after the element,
and prepend/append inner content. Both servers stream request and response bodies, and the correctness pass requires
byte-identical output before timing.

## Maintained inventory

Benchmark sources are grouped by workload under `benchmarks/AngleSharp.ReadOnlyDom.Benchmarks/Suites`. Infrastructure,
manual measurements, and correctness runners live under `Support`.

| Tier | Maintained entry points | Contract |
| --- | --- | --- |
| `small`, `full` | `CorpusBenchmark` | Mature AngleSharp versus read-only and compact DOM over checked-in pages |
| `compact` | `CompactBuildBenchmark` | Read-only DOM versus compact layouts and parser reuse |
| `compact-stages` | `CompactBuildStageBenchmark` | Arena setup/rent, tree construction, and frozen publication costs |
| `extraction` | `ExtractionRowScaleBenchmark`, `LongSyntheticConstructionBenchmark`, `QqArticleScraperBenchmark` | Equivalent owned results across materialized and construction-time extraction |
| `rows` | `ExtractionRowScaleBenchmark` | Many-rows extraction where cost scales with rows x fields rather than with locating one result |
| `scraping` | `LongSyntheticConstructionBenchmark`, `QqArticleScraperBenchmark` | End-to-end target-near-EOF and real-page scraping |
| `query` | `HttpClientStreamingQueryBenchmark`, `QueryWorkloadRunner` | Deterministic socket streaming plus repeated representative query workloads |
| `utf8` | `Utf8TokenizerBaselineBenchmark`, `Utf8RodomBenchmark`, `Utf8DomProjectionBenchmark` | Validated wire-byte tokenizer, compact construction, and mutable-DOM projection paths |
| `utf8-baseline` | `Utf8TokenizerBaselineBenchmark`, `Utf8TokenizerBaselineRunner` | Frozen tokenizer state/pathology matrix over memory and segmented input |
| `retained` | `RetainedMemoryRunner` | Forced-GC retained size and parse allocation |

The curated set intentionally excludes historical implementation probes. Add a new benchmark only when it establishes a
new stable workload or regression contract; temporary optimization experiments should extend the nearest maintained suite
and be removed after the decision.

## Workload notes

`CorpusBenchmark` uses checked-in snapshots under `tests/AngleSharp.ReadOnlyDom.Tests/temp`. `small` selects five
representative pages; `full` runs the complete corpus.

`LongSyntheticConstructionBenchmark` generates a deterministic 1.96 MB page with 5,000 irrelevant attribute-heavy
sections and a small result near EOF. Every lane consumes the complete input, so the comparison measures avoided
materialization rather than early termination.

`QqArticleScraperBenchmark` has two parameters: the checked-in 125 KB QQ page producing 16 owned articles and a 4x body
variant producing 64. Its 16 lanes are categorized as `Natural`, `Optimized`, or `QqSpecificUpperBound` so ordinary
public-API usage is not conflated with a deliberately scraper-specific maximum:

- natural stock AngleSharp + CSS, unfiltered read-only DOM, unfiltered compact DOM, and completed-element streaming;
- scraper-configured AngleSharp from UTF-16 and UTF-8, with an optional body token filter;
- optimized read-only DOM and identity-resolved compact DOM from UTF-16 and UTF-8;
- the same `ul.news-list` pre-DOM filter applied to mutable, read-only, and compact builders;
- contiguous and bounded 4 KiB segmented UTF-8 folds, plus the completed-element query.

The `ul.news-list` lanes are QQ-specific by design: the tokenizer consumes the complete input, but only matching
subtrees reach the tree builder. They measure the maximum benefit of construction-time filtering, not a general DOM
parse. The segmented fold uses a bounded `Pipe` (16 KiB pause / 8 KiB resume thresholds) so it includes real async
producer-consumer and segment-boundary costs.

Global setup requires every lane to return the same `List<Article>` values.

`HttpClientStreamingQueryBenchmark` serves deterministic HTML over a loopback HTTP/1.1 socket and compares response
materialization with `ResponseHeadersRead -> stream -> PipeReader -> query`. It excludes DNS, TLS, CDN, and public-network
jitter while retaining real `HttpClient` and socket behavior.

`Utf8TokenizerBaselineBenchmark` freezes equal-size typical, malformed, raw-text, entity-heavy, long-token, and duplicate
attribute workloads. Memory and 4 KiB segmented inputs must consume the same bytes and produce the same chunk-insensitive
token fingerprint.

## Correctness checks

Run focused correctness gates without BenchmarkDotNet:

```powershell
dotnet run --project benchmarks/AngleSharp.ReadOnlyDom.Benchmarks -c Release -f net10.0 -- --qq-scraper-check qq.html
dotnet run --project benchmarks/AngleSharp.ReadOnlyDom.Benchmarks -c Release -f net10.0 -- --qq-scraper-check qq-x4.html
dotnet run --project benchmarks/AngleSharp.ReadOnlyDom.Benchmarks -c Release -f net10.0 -- --utf8-token-smoke
dotnet run --project benchmarks/AngleSharp.ReadOnlyDom.Benchmarks -c Release -f net10.0 -- --utf8-dom-check
```

For tokenizer regression discovery, run the coverage-guided differential mutator. It compares every candidate with
AngleSharp, then replays it bytewise and under fixed and jittered chunk layouts. Divergences are minimized and written
to `artifacts/fuzz` together with the seed and both token streams:

```powershell
dotnet run --project benchmarks/AngleSharp.ReadOnlyDom.Benchmarks -c Release -f net10.0 -- --utf8-tokenizer-fuzz --iterations 10000 --seed 12345
dotnet run --project benchmarks/AngleSharp.ReadOnlyDom.Benchmarks -c Release -f net10.0 -- --utf8-tokenizer-fuzz --repro artifacts/fuzz/<run>/<case>.html
```

Use `--corpus <file-or-directory>` to seed mutations from a PR-specific corpus, `--max-failures` to control unique
findings, and `--output` to select the reproducer directory. A non-zero exit code means at least one differential or
chunking-invariance failure was found. Differential findings are review candidates rather than automatic proof that the
streaming side is wrong: minimize and check the HTML tokenizer specification when the AngleSharp oracle itself has an
edge-case discrepancy.

The UTF-8 baseline tier also writes a diagnostics report containing maximum buffered token bytes and per-state byte
visits. Diagnostic visits may exceed source length because bytes reprocessed by multiple states are counted each time.

## Results and acceptance

Curated runs write ignored output under `artifacts/benchmarks/<timestamp>-<commit>-<tier>/`. ShortRun results are
directional; allocation is the primary micro gate. Accept throughput changes using equivalent output and allocation
contracts, sustained paired measurements, and representative real-page or query workloads. Preserve rejected conclusions
in the commit or issue that made the decision rather than retaining another permanent benchmark class.
