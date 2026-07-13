# Issue #35: long synthetic construction-time extraction benchmark

## Purpose

Measure the workload construction-time extraction is intended to improve: a long HTML document with a small selected
result near EOF and a large amount of irrelevant topology, text, and attribute data before it.

This is an avoided-construction benchmark, not an avoided-tokenization benchmark. Every method receives the same rooted
`string`, consumes the complete input, and uses AngleSharp's HTML tokenizer and tree builder. The target's EOF position
also prevents the specialized first-match path from benefiting from early termination.

## Deterministic fixture

`LongSyntheticConstructionBenchmark` generates the page during global setup rather than checking in a multi-megabyte
fixture. Its fixed 5,000 noise blocks produce:

- about 1.96 MB of UTF-8 HTML;
- 5,000 attribute-heavy irrelevant `section` subtrees;
- 75,241 processed tokens and 50,171 topology nodes;
- a 32-paragraph `article#content` target near EOF.

Global setup executes all four implementations and requires byte-for-byte equal returned strings before measurement.

## Compared paths

1. Read-only DOM parse followed by traversal and normalized text extraction.
2. Compact DOM materialization followed by a compiled extraction plan.
3. Specialized query-directed normalized-text extraction during construction.
4. General EOF aggregate normalized-text extraction during construction.

Run it with:

```powershell
./scripts/bench.ps1 long-streaming
```

The script treats a BenchmarkDotNet report with no workload results as a failure, even when BenchmarkDotNet itself exits
with code zero.

## Result interpretation

ShortRun timing confidence intervals can be wide; `Mean` and total `Allocated` are the useful signals. Because tokenizer
and tree-builder work is common to all four methods, expect a much larger allocation win than runtime win. The benchmark
does not demonstrate bounded-memory stream input.

## Commit-pinned result

Implementation commit: `2292328`

Artifact: `artifacts/benchmarks/20260713-222924-2292328-long-streaming`

| Method | Mean | Allocated | Relative time | Relative allocation |
| --- | ---: | ---: | ---: | ---: |
| Read-only parse and traverse | 40.43 ms | 11.21 MB | 1.00 | 1.00 |
| Compact parse and plan | 41.70 ms | 6.12 MB | 1.03 | 0.55 |
| Query-directed construction | 33.53 ms | 1.42 MB | 0.83 | 0.13 |
| EOF aggregate construction | 33.50 ms | 1.42 MB | 0.83 | 0.13 |

Both construction-time paths avoid Gen0 and Gen1 collections in this run. Against the read-only baseline they reduce
managed allocation by about 87% and mean runtime by about 17%, despite paying the full tokenization and tree-builder
cost. The compact result confirms that compact representation alone removes substantial allocation; pushing the
projection into construction removes the remaining irrelevant value materialization.
