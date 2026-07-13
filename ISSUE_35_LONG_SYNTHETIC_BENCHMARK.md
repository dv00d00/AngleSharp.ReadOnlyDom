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

Authoritative commit-pinned results will be recorded here after the implementation commit.
