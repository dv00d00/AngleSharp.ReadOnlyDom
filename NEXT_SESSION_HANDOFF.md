# Next session handoff: issue #15 extraction plan

## Resume here

Repository: `C:\Users\Dmitry\RiderProjects\AngleSharp.ReadOnlyDom`

Branch: `codex/issue-15-extraction-plan`

Base commit: `6463ab8` (`Merge pull request #28 ... issue-14-subtree-boundaries`)

The working tree intentionally contains uncommitted issue #15 work. Do not reset, clean, switch away, or overwrite it.
Start with:

```powershell
cd C:\Users\Dmitry\RiderProjects\AngleSharp.ReadOnlyDom
git branch --show-current
git status --short
git diff --check
```

The expected branch is `codex/issue-15-extraction-plan`.

## Objective

Finish GitHub issue #15, **Compile a minimal explainable extraction plan over the compact tree**.

The intended scope is the smallest interpreted plan justified by the workload work:

- tag, ID, class-token, attribute-exists, and attribute-equals predicates;
- descendant and direct-child paths;
- first/all cardinality;
- attribute and normalized-subtree-text projections;
- required/optional field validation;
- inspectable plan requirements, explanation, and execution counters;
- explicit borrowed versus owned result values;
- comparison with read-only traversal and hand-written compact scans;
- no CSS parser, generated IL, generic optimizer, mutation support, or tokenizer/tree-builder integration.

## Current implementation

New `AngleSharp.ReadOnlyDom.Compact/CompactExtractionPlan.cs` contains the prototype:

- fluent `CompactExtractionPlan.Start(...).With...().Select...().Compile()` builder;
- interpreted descendant and direct-child path execution;
- document binding through `CompactBoundExtractionPlan`, resolving tag and attribute IDs once outside hot loops;
- first/all cardinality;
- required and optional attribute fields;
- borrowed document slices and explicitly owned values;
- owned normalized-subtree-text projection;
- requirements for inspected, retained, and materialized attributes plus metadata sidecars;
- `Explain()` output and execution counters.

`CompactDocument.TryGetAttribute(...)` was added as an internal counted lookup for the interpreter.

The implementation deliberately materializes candidate lists between path steps. A duplicate-handle set protects
multi-parent descendant paths. `TakeFirst()` currently evaluates the path candidate set and stops after the first valid
projection; it does not yet terminate candidate discovery early. The explanation and design note state this explicitly.

Required attribute semantics were corrected at the end of the implementation: only an absent attribute rejects a row.
An existing empty attribute is a valid required value. The final full test gates cover this semantic change.

## Tests added

`AngleSharp.ReadOnlyDom.Tests/DirectCompactArenaTests.cs` now covers:

- all predicate/path/projection families in Packed and FrozenColumns layouts;
- explanation, requirements, counters, and ownership tags;
- malformed foster-parenting and formatting-adoption cases against AngleSharp's object DOM;
- required versus optional attributes, including an existing empty attribute.

The final post-format validation passed:

- Release solution build: 0 warnings, 0 errors;
- .NET 10: **179,172 passed**, 0 failed, 0 skipped;
- .NET Framework 4.7.2: **59,729 passed**, 0 failed, 0 skipped;
- `git diff --check`: clean.

The test host must run outside the managed filesystem sandbox because its IPC pipes are blocked there. This is an
environment limitation, not a product failure.

## Benchmark work

New `AngleSharp.ReadOnlyDom.Benchmarks/CompactExtractionPlanBenchmark.cs` compares query-only execution on one retained
document for:

1. read-only DOM traversal;
2. hand-written compact scan;
3. bound interpreted compact plan.

`scripts/bench.ps1 plan` was added. All benchmark execution remains Server GC. `Program.cs` now uses
`Job.ShortRun.WithGcServer(true)` globally; this fixes an existing mismatch where the script/documentation claimed
ShortRun but the configured job was BenchmarkDotNet's long default.

Authoritative completed report:

`artifacts/benchmarks/20260713-191809-6463ab8-plan/plan/results/AngleSharp.ReadOnlyDom.Benchmarks.CompactExtractionPlanBenchmark-report-github.md`

Server-GC ShortRun result:

| Method | Mean | Allocated |
| --- | ---: | ---: |
| Read-only traversal | 40.29 us | 9.58 KB |
| Hand-written compact scan | 21.95 us | 12.46 KB |
| Bound interpreted compact plan | 17.93 us | 12.92 KB |

Interpret time is directional because ShortRun confidence intervals are wide. The firmer result is the approximately
0.46 KB execution allocation premium over hand-written compact code. The bound plan resolves IDs once, which plausibly
explains why interpretation is competitive on this representative query.

Earlier `191259`, `191253`, and `191543` plan artifact directories are failed, partial, or superseded attempts. Do not use
them as final evidence. A timed-out long BenchmarkDotNet process was explicitly stopped; the completed `191809` run is the
one to cite.

## Documentation already drafted

- `ISSUE_15_EXTRACTION_PLAN.md` records scope, API example, ownership, correctness, benchmark results, and current limits.
- `QUERY_DIRECTED_ENGINE_DIRECTION.md` links the compact-tree experiment without conflating it with issue #17.
- `BENCHMARKING.md` lists the new `plan` tier.

Review these after final implementation changes and keep the numbers synchronized with the authoritative report.

## Working-tree inventory

Modified tracked files:

- `AngleSharp.ReadOnlyDom.Benchmarks/Program.cs`
- `AngleSharp.ReadOnlyDom.Compact/CompactDocument.cs`
- `AngleSharp.ReadOnlyDom.Tests/DirectCompactArenaTests.cs`
- `BENCHMARKING.md`
- `QUERY_DIRECTED_ENGINE_DIRECTION.md`
- `scripts/bench.ps1`

New untracked files:

- `AngleSharp.ReadOnlyDom.Benchmarks/CompactExtractionPlanBenchmark.cs`
- `AngleSharp.ReadOnlyDom.Compact/CompactExtractionPlan.cs`
- `ISSUE_15_EXTRACTION_PLAN.md`
- `NEXT_SESSION_HANDOFF.md`

Benchmark artifacts are ignored and should not be committed.

## Delivery continuation

The implementation, review, formatting, build, and test gates are complete. The remaining delivery steps are to commit
the coherent issue #15 change, push the branch, open a PR with `Closes #15`, merge it, and verify issue #15 is closed.

Rerun `./scripts/bench.ps1 plan` only if execution code or benchmark shape changes. Otherwise preserve the completed
`191809` result; repeated ShortRun timing is noisy.
## Decision guardrails

- Keep the plan interpreted and explainable.
- Keep storage/name-ID resolution outside hot loops by binding once per document.
- Do not overclaim the ShortRun timing advantage; allocation and correctness are stronger evidence.
- Keep borrowed and owned value lifetimes explicit.
- Do not merge until the final tests pass after the latest required-field semantic change.
- Issue #17 remains the separate streaming/query-directed tree-builder experiment.
