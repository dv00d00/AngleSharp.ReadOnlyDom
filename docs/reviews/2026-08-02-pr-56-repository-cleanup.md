# Pull request #56: Repository structure and cleanup review

Reviewed commit: `4e86c68342b1295a7f2c3ecb5ac878514ef97493`

Desired pre-alpha end state:

- no dead or redundant project survives for historical reasons;
- every source project has a concise use case and dependency direction;
- library assemblies contain mechanisms, while opinionated products and converters remain samples;
- physical folders expose the same boundaries as the runtime architecture;
- a clean checkout can reproduce the supported build without stale branch instructions.

## Current project roles

| Project | Current role | Recommended disposition |
| --- | --- | --- |
| `AngleSharp.ReadOnlyDom` | Object-backed read-only DOM over AngleSharp construction | Keep; organize parsing, document model, query, and infrastructure |
| `AngleSharp.ReadOnlyDom.Compact` | Columnar retained DOM plus EOF row projection | Keep; remove Streaming dependency and distinguish retained query from EOF projection |
| `AngleSharp.ReadOnlyDom.Streaming` | Standalone native UTF-8 tokenizer and bounded lexical query engine | Keep only after its structural contract is made explicit |
| `AngleSharp.ReadOnlyDom.Helpers` | Old HTTP download/character-buffer helpers | Delete; there are no consumers |
| `AngleSharp.ReadOnlyDom.Samples` | Demonstrates all representation lanes | Keep, but use unambiguous lane names and current APIs |
| `AngleSharp.ReadOnlyDom.MarkdownProxy` | Opinionated Markdown/text converter, browser UI, fetch and asset proxy | Reduce to a focused safe conversion sample or split demonstrations |

## P1: `AngleSharp.ReadOnlyDom.Helpers` is a dead project

Files:

- `src/AngleSharp.ReadOnlyDom.Helpers/ArrayPoolExtensions.cs`
- `src/AngleSharp.ReadOnlyDom.Helpers/HttpClientExtensions.cs`
- `src/AngleSharp.ReadOnlyDom.Helpers/RingBuffer.cs`
- `src/AngleSharp.ReadOnlyDom.Helpers/AngleSharp.ReadOnlyDom.Helpers.csproj`

Repository-wide symbol and namespace searches found no consumer outside the project. The benchmark project references the
assembly but does not use its namespace or types. The project also carries `Microsoft.IO.RecyclableMemoryStream`,
`Microsoft.Extensions.ObjectPool`, and AngleSharp dependencies solely for this unused code.

Recommended end state:

- delete the project;
- remove it from `AngleSharp.ReadOnlyDom.slnx` and benchmark project references;
- remove `InternalsVisibleTo` from the core project;
- remove package versions that have no remaining consumer.

If HTTP downloading is needed later, implement it at the application boundary with explicit cancellation, byte limits,
encoding policy, and ownership rather than restoring this generic helper bucket.

## P1: dead and example-only code remains in the core library

Proven dead files:

- `src/AngleSharp.ReadOnlyDom/SmallReferenceList4.cs` — referenced only by a size assertion;
- `src/AngleSharp.ReadOnlyDom/SpanExtensions.cs` — no consumers;
- `src/AngleSharp.ReadOnlyDom/Filters/TokenDelegateFilter.cs` — public type with no consumer.

Example/benchmark-only filters:

- `FirstTagAndAllChildren` is used by benchmarks and one test, not by production library code;
- `OnlyElementWithIdAndDescendants` is used only by a test.

These filters also count lexical tag depth rather than HTML tree scopes, so presenting them as core library primitives
overstates their correctness.

Recommended end state:

- delete dead types and their size-only tests;
- move benchmark-specific filtering into `benchmarks/.../Support`;
- delete the test-only filter or make it private test support;
- remove the public delegate and filter namespace if nothing remains.

## P2: one public interface exists only to split one member

Files:

- `src/AngleSharp.ReadOnlyDom/Html/Model/IPrintable.cs`
- `src/AngleSharp.ReadOnlyDom/Html/IReadOnlyNode.cs`

`IPrintable` has no independent consumer; only `IReadOnlyNode` derives from it. It also places a public contract in the
internal-model folder.

Recommended end state: put `Print(TextWriter)` directly on `IReadOnlyNode` and remove `IPrintable`, unless an actual
non-node printable abstraction is introduced.

## P2: project dependency direction contains dead edges

Current relevant edges:

```text
ReadOnlyDom.Compact -> ReadOnlyDom
ReadOnlyDom.Compact -> ReadOnlyDom.Streaming   (unused)
ReadOnlyDom.Helpers -> ReadOnlyDom             (unused project)
Benchmarks -> Helpers                          (unused)
Streaming -> InternalsVisibleTo(Compact)       (unused)
```

Recommended graph:

```text
ReadOnlyDom.Compact -> ReadOnlyDom -> AngleSharp fork
ReadOnlyDom.Streaming -> System.IO.Pipelines
Samples/Benchmarks/Tests -> the lanes they exercise
```

Streaming should remain standalone. Compact should not depend on it merely because both projects process HTML.

## P2: the object-DOM project still has a flat miscellaneous root

The Compact reorganization establishes useful lane-oriented folders. The object DOM still mixes parser entry points,
metadata policy, query extensions, shims, generated data, and small storage helpers at its root.

Recommended physical layout, preserving namespaces unless a public rename is intentional:

```text
src/AngleSharp.ReadOnlyDom/
  Document/          public read-only contracts and internal model
  Parsing/           ReadOnlyParser, construction factory, metadata profiles
  Query/             QueryHelpers
  Infrastructure/    surviving shims and small storage helpers
  Generated/         GeneratedTagMetadata.g.cs
```

Do this only after deleting dead files. Moving unused code into attractive folders would make the repository look cleaner
without making it simpler.

## P2: sample and README terminology is stale

Locations:

- `samples/AngleSharp.ReadOnlyDom.Samples/README.md:16`
- `samples/AngleSharp.ReadOnlyDom.Samples/README.md:20`
- `samples/AngleSharp.ReadOnlyDom.Samples/README.md:29`
- `readme.md:34`
- `readme.md:35`

Problems:

- EOF construction projection is called `COMPACT STREAMING`, although it consumes a complete rooted string and evaluates
  after parsing;
- the sample still promises Compact Markdown after that API was removed;
- clone instructions point to old feature branches rather than the current paired branches.

Recommended end state:

- reserve `Streaming` for bounded UTF-8 input;
- call the Compact lane `EOF projection` or the selected new API family name;
- keep Markdown only in the Markdown sample;
- make build instructions name the currently supported AngleSharp/ReadOnlyDom pair, or pin exact commits.

## P2: source-override build is not self-verifying in pull requests

The object and Compact projects require unreleased AngleSharp construction APIs. `Directory.Build.targets` is intentionally
ignored, and a fresh checkout builds against AngleSharp 1.7.0 unless the example targets file is copied or an equivalent
property is imported. Pull request #56 has no GitHub status checks.

The local review initially reproduced exactly this failure before explicitly importing the source override. A correct
build should make the selected dependency visible and fail early when an incompatible package is used.

Recommended end state:

- add CI that checks out the matching AngleSharp commit/branch;
- set `AngleSharpSourceRoot` explicitly;
- restore and build the complete supported matrix;
- assert the source project reference was selected;
- run the net10 test suite and a smaller focused gate for PR feedback.

## P2: formatting policy remains unpinned

Commit `4e86c68` reformats 26 files with CSharpier 1.3.0, including unrelated Streaming tokenizer and test files. The
repository contains `.csharpierrc.json` but no local tool manifest pinning the formatter version.

Recommended end state:

- either drop the unrelated formatting commit from this cleanup PR;
- or add a `.config/dotnet-tools.json` manifest and document `dotnet tool restore` plus the exact check command;
- keep future behavior changes separate from repository-wide formatting.

## P3: existing build warnings contradict the clean target

The verified Release build succeeds but emits three `TUnit0046` warnings:

- `tests/AngleSharp.ReadOnlyDom.Tests/TopLevelSmoke.cs:434` for net472 and net10;
- `tests/AngleSharp.ReadOnlyDom.Tests/TopLevelArenaSmoke.cs:78` for net10.

The data sources return mutable `SelectorTestCase` instances. Either return factories as requested by TUnit or make the
test-case representation genuinely immutable in a form accepted by the analyzer. Once fixed, enable warnings-as-errors
for repository projects where practical.

## Verification baseline

The exact paired source revisions were validated in isolated sibling worktrees:

- AngleSharp: `4819b43af` (`origin/devel` at review time);
- AngleSharp.ReadOnlyDom: `4e86c68342b1295a7f2c3ecb5ac878514ef97493`;
- Release solution build: succeeded with 0 errors and 3 existing warnings;
- net10 test suite: 179,299 passed, 0 failed, 0 skipped;
- pull request status checks: none configured.

The first isolated build without the ignored source-override targets correctly demonstrated that the public AngleSharp
1.7.0 package is insufficient. That was an environment/setup failure, not a defect introduced by pull request #56.

## Recommended cleanup sequence

1. Delete `AngleSharp.ReadOnlyDom.Helpers`, dead core types, and dead dependency edges.
2. Fix Compact and Streaming correctness findings with focused regression tests.
3. Remove unsafe proxy/browser behavior from the Markdown sample.
4. Rename the Compact EOF projection lane and reduce both public surfaces.
5. Reorganize only the surviving object-DOM files.
6. Replace stale review/direction documents with stable architecture contracts.
7. Pin tooling, add paired-repository CI, and make the build warning-free.

Do not combine all seven steps into one behavioral commit. The end state should be one coherent branch, but the history
should separate deletions, correctness fixes, API changes, moves, and formatting so each can be reviewed independently.
