# Benchmarking

.NET 10 is the only performance runtime. Tests run on .NET 10 and .NET Framework 4.8.1; the library still builds for
.NET 8 and netstandard2.0, but those targets do not have dedicated benchmark or test executions.

Run a tier from the repository root:

```powershell
./scripts/bench.ps1 micro
./scripts/bench.ps1 small
./scripts/bench.ps1 retained
./scripts/bench.ps1 full
./scripts/bench.ps1 all
```

Run the anonymous three-document fat-page comparison directly:

```powershell
dotnet run --project AngleSharp.ReadOnlyDom.Benchmarks -c Release -f net10.0 -- `
  --filter "*FatDocumentParsingBenchmark*" --job short
```

It selects the three largest checked-in HTML fixtures as `LargeA`, `LargeB`, and `LargeC`, then compares default
AngleSharp, the production read-only DOM, and the frozen arena. Setup verifies equal element counts including template
contents and fails if an arena document falls back to packed layout.

Every run writes ignored, versioned output under `artifacts/benchmarks/<timestamp>-<commit>-<tier>/`, including commit,
runtime, job, corpus, and noise metadata. `micro` is the GitHub partial-parse benchmark with full and filtered paths.
`small` parses five representative checked-in pages. `full` parses all checked-in pages. `retained` runs the small
forced-GC retained-memory measurement without running BenchmarkDotNet.

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
