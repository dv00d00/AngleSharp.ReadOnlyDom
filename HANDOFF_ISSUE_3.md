# Handoff: issue #3 metadata profiles

Issue: https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/3  
Starting commit: `09a5d6f` (`main`, clean when this handoff was written)

## Goal

Make optional DOM information and its cost explicit without creating another complete node hierarchy. Preserve the current
allocation-optimized representation as the zero-cost default and move toward document-owned optional state where that can
be done without adding a reference field to every node.

Read `ARCHITECTURE_REVIEW.md`, especially “Explicit feature profiles”, “Document-owned metadata”, “Source assignment”, and
“Correctness contract of the minimal DOM”. Read `BENCHMARKING.md` before changing representation or construction.

## Decisions already made

- Keep one structural DOM hierarchy. Do not add a full parallel hierarchy per metadata level and do not start with
  `Node<TPayload>`.
- .NET 10 is the only performance runtime.
- Tests execute on `net10.0` and `net481`. The library still builds for `net8.0` and `netstandard2.0`, but there are no
  dedicated .NET 8 test or benchmark executions.
- The minimal profile must not allocate a metadata container and must not gain a per-node field.
- Owner-document lookup, browser lifecycle, error retention, and scripting are deliberately absent from today's minimal
  contract. Parent navigation remains available because AngleSharp construction and the public traversal API use it.
- Namespace is already derived from `NodeFlags`; do not store it.
- Text projection is a separate tokenizer-driven project, not a metadata profile.
- Correctness and benchmark issues #2 and #4 are complete. Do not reopen their design unless measurements or tests expose
  a concrete problem.

## Current implementation

`ReadOnlyElement` currently uses one process-global `ConditionalWeakTable<ReadOnlyElement, OptionalMetadata>` for sparse
prefix and `ISourceReference` storage. This was an allocation-compatible bridge, not the desired final ownership model.
Minimal unprefixed parsing does not create entries. Prefixes and source references do.

Important details:

- `NamespaceUri` is derived from HTML/SVG/MathML flags.
- `LocalName` is derived by slicing the qualified `NodeName` when a prefix exists.
- Shallow copies preserve prefixes and independently clone attributes/template content.
- `SourceReference` must remain writable internally because AngleSharp assigns it during construction when
  `HtmlParserOptions.IsKeepingSourceReferences` is enabled.
- `ReadOnlyDocument.TrackError` currently discards errors deliberately.
- Nodes intentionally have no owner-document field. A document-owned source store therefore cannot be reached directly
  from `element.SourceReference = ...` without a bridge, a tracked-element field/subtype, or an upstream factory hook.

## Recommended implementation sequence

1. Define the public configuration and capability contract first. Suggested presets are `Minimal`, `Navigable`,
   `SourceMapped`, and `Diagnostic`, backed by orthogonal internal flags. Precisely document what each enables and what it
   costs. Avoid claiming that `Minimal` removes parent links unless a measured post-construction compaction step is added.
2. Make configuration instance-scoped through the construction factory/context. Preserve `ReadOnlyParser.DefaultContext`
   behavior and add a clear way to create a context/parser for a profile. Avoid mutable global profile state.
3. Implement `Diagnostic` first: errors can live directly on `ReadOnlyDocument`, so this proves opt-in document-owned
   metadata without changing node layout. Add an explicit capability API instead of making minimal return a misleading
   empty diagnostics collection.
4. Map comments and processing instructions to tokenizer options. Treat them as independent features rather than implied
   side effects of diagnostics.
5. Spike source fidelity separately (`Offsets`, `Positions`, `Tokens`). Measure AngleSharp's existing token/source-reference
   objects before exposing a public contract. Compact offsets are preferable if the construction API supplies them.
6. Move prefix/source storage only after solving assignment routing. The cleanest long-term design remains an upstream
   construction-factory hook such as `SetSourceReference(element, ref token)`. If an in-repo bridge is required, benchmark
   a sparse tracked-element subtype or sparse sink association; do not add a document/sink pointer to every minimal node.
7. Benchmark every preset independently and report both parse allocation and retained memory.

## Unresolved design questions

- What does `Navigable` add beyond today's parent/child navigation: owner-document lookup, sibling indexes, or both?
- Should source metadata be queryable only through a document capability (`document.Metadata.TryGet...`) or also through
  element convenience APIs? Prefer the document capability unless convenience can remain zero-cost for minimal nodes.
- Is an upstream AngleSharp hook acceptable now, or should the first release retain the weak-table bridge while making the
  profile/capability contract explicit?
- Are prefixes sufficiently rare that the existing sparse weak-table entry is acceptable, or should prefix be derived from
  `NodeName` and namespace context? Do not remove it without SVG/MathML qualified-name tests.
- Diagnostics need a retention policy: all exceptions, capped count, counts by category, or callback-only.

## Required tests

- Minimal creates no metadata store/entry for representative unprefixed HTML.
- Every preset advertises exactly the capabilities it supports.
- SourceMapped and Diagnostic have focused positive and minimal-profile negative tests.
- Prefix, namespace, local name, and source fidelity are correct for HTML, SVG, and MathML.
- Malformed HTML diagnostics do not change permissive construction behavior.
- Existing contract/differential tests remain green.
- Full suite: `166,075` tests were passing on each active test runtime at handoff.

## Performance gates and commands

```powershell
csharpier check .
dotnet build AngleSharp.ReadOnlyDom.slnx -c Release --no-incremental
dotnet test AngleSharp.ReadOnlyDom.Tests/AngleSharp.ReadOnlyDom.Tests.csproj -c Release -f net10.0 --no-build
dotnet test AngleSharp.ReadOnlyDom.Tests/AngleSharp.ReadOnlyDom.Tests.csproj -c Release -f net481 --no-build
./scripts/bench.ps1 micro
./scripts/bench.ps1 retained
./scripts/bench.ps1 all
```

Use `micro` while iterating, `retained` for ownership changes, and `all` before closing the issue. Current exact-commit
artifacts for `09a5d6f` are under ignored `artifacts/benchmarks/`; regenerate locally rather than committing them.

Key baseline signals on .NET 10:

| Tier | Read-only time | Read-only allocated | Read-only retained |
| --- | ---: | ---: | ---: |
| Micro full | 4.817 ms | 706.51 KB | — |
| Micro filtered | 3.858 ms | 212.08 KB | — |
| Small corpus | 534.2 ms | 65.68 MB | 62.17 MB |
| Full corpus | 636.1 ms | 77.71 MB | 72.01 MB |

Investigate repeatable increases above 1% micro allocation, 3% corpus allocation/retained memory, or 5% time outside
overlapping noise. Profile costs may intentionally increase, but `Minimal` must not regress.

## Completion checklist

- Allocation/behavior contract for every preset is in repository documentation.
- Minimal has no metadata container allocation and no new per-node field.
- SourceMapped and Diagnostic focused tests pass.
- HTML/SVG/MathML metadata behavior is correct.
- Exact-commit `./scripts/bench.ps1 all` completes and reports every preset.
- Solution builds; exhaustive tests pass on `net10.0` and `net481`.
- Issue #3 contains the final benchmark summary and is moved to Done only after all gates pass.
