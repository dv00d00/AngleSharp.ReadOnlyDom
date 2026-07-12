# Next-session handoff

Delete this temporary file after the next session has consumed it and completed the follow-up work.

## Repository state

- Working directory: `C:\Users\Dmitry\RiderProjects\AngleSharp.ReadOnlyDom`
- Branch: `codex/direct-compact-construction`
- Latest commit: `5ca7a1c Finish compact DOM payload storage experiment`
- The branch is local; it has not been pushed and has no pull request.
- Issue #6 is closed on GitHub, but this branch contains the later follow-up experiments.

Relevant commits:

- `c4154ed` — direct compact construction prototype.
- `6003e39` — parser reuse, nullable arena collections, and inline child storage.
- `7639a4b` — collection-shape measurement and initial handoff.
- `5ca7a1c` — source-backed compact values, exact arrays, selectable source indexes, roadmap note, and `SmallReferenceList2` rename.

## Agreed product and lifetime model

The long-term owning compact parser should resemble `System.Text.Json.JsonDocument`:

- parse, extract, dispose is the primary workload;
- the document owns or anchors its input source;
- text and attribute values normally remain source slices;
- decoded entities and parser-synthesized values use a compact side arena;
- pooled storage is released by document disposal;
- values escaping the document lifetime must copy or explicitly transfer ownership.

The build-from-ROD `CompactDocument` cannot silently take ownership of the source DOM. Its new `ReadOnlyMemory<char>` values are borrowed and require the source backing storage to remain valid. Independent ownership belongs in `DirectCompactParser`, where source ownership can be transferred during parsing.

Issue #6 remains an optimization/fallback representation for the existing read-only DOM. The much larger query-directed engine is a separate direction documented in `QUERY_DIRECTED_ENGINE_DIRECTION.md` and GitHub issues #13–#17.

## Current performance status

Short .NET 10 measurements on the GitHub fixture:

| Operation | Time | Allocated |
| --- | ---: | ---: |
| ROD Minimal parse | about 5.08 ms | 933 KB |
| Build source-backed compact from parsed ROD | 857.5 us | 1,012.53 KB |
| Direct pooled compact parse, earlier fair run | about 5.7 ms | about 1,508 KB |

The temporary single-pass `List<T>` source-backed builder allocated 1,517.85 KB. Restoring an exact structural sizing pass reduced this to 1,012.53 KB. The pass counts nodes, attributes, and non-empty value references; it does not count/copy text characters.

Earlier five-page retained results, before the latest source-backed build change:

| Representation | Retained | Approximate retained/node |
| --- | ---: | ---: |
| ROD Minimal | 62.16 MB | 102.5 B |
| Build-then-compact | 42.74 MB | 70.5 B |
| Direct 16-byte hot compact | 40.67 MB | 67.0 B |

These establish the final-layout promise but must not be presented as current source-backed retained results. Run a fresh retained-memory comparison next.

The four-inline-child ROD change reduced five-page parse allocation from about 66.01 MB to 65.20 MB while slightly increasing retained memory from 62.16 MB to 62.25 MB. It is a modest win for parse/extract/dispose.

Halving the compact hot record from 32 to 16 bytes did not materially improve a simple linear kind scan. Integer name IDs produced the large element-scan improvement. Do not attribute it to record width alone.

## Payload index result

`CompactDomOptions.SourceLocationIndexMode` supports `None`, `Dense`, `Sparse`, and `Dictionary`. Legacy `CompactMetadataOptions.SourceLocations` selects dense storage.

On the SourceMapped GitHub fixture, scanning every node:

| Index | Lookup | Build allocation |
| --- | ---: | ---: |
| Dense | 4.75 us | 1.127 MB |
| Sparse binary search | 114.46 us | 1.127 MB |
| Dictionary | 10.26 us | 1.158 MB |

Source locations are too dense on this workload for sparse storage. Dense is the current choice. Keep sparse for genuinely rare immutable payloads and dictionary for sparse frequent random lookup only when measurements justify it. Do not create a generic column framework yet.

## Validation

After commit `5ca7a1c`:

- CSharpier passed all 83 files.
- Release solution build passed all targets.
- .NET 10 exhaustive tests: 166,103 passed.
- net481 exhaustive tests: 166,090 passed.
- Known pre-existing nullable/TUnit warnings only.

## Recommended immediate next work

1. Rerun the small/full retained-memory runner for the latest source-backed `CompactDocument`. Compare logical payload bytes, retained source size, total retained memory, and peak construction memory.
2. Add an explicit lifetime test/documentation for borrowed build-from-ROD values, including string, `ReadOnlyMemory<char>`, and char-array parser inputs. Determine whether any source implementation can invalidate borrowed memory on disposal.
3. Decide whether to keep the build view borrowed-only or add a clearly named detached/copying mode. Do not make ownership implicit.
4. In the direct parser, prototype ownership transfer of `TextSource` plus source-range/fallback-arena value references. This is the actual `JsonDocument`-style path.
5. Profile direct construction allocations by type before more layout work. Wrappers and `NodeState` remain the primary suspects.
6. If continuing the larger product direction, start with GitHub issue #13 rather than implementing a broad DSL.

## GitHub roadmap after #6

- #13 — Establish query-directed extraction workloads and baselines.
- #14 — Add preorder subtree boundaries and forward compact-tree scans.
- #15 — Compile a minimal explainable extraction plan over the compact tree.
- #16 — Prototype an opaque-handle AngleSharp tree-construction sink.
- #17 — Prototype streaming extraction and query-directed retention.
- #7 remains the separate tokenizer-driven semantic text-projection investigation.

The first decisive larger-product query should be:

```text
first div#content -> normalized subtree text
```

Compare ROD traversal, compact preorder scan, tree-builder-integrated extraction without a retained DOM, and a hand-written streaming baseline.

## Temporary experiments to preserve until cleanup

Do not remove apparently losing paths during measurement:

- `AngleSharp.ReadOnlyDom.CompactPrototype/`;
- direct owned and pooled final buffers;
- list-backed and pooled arena buffers;
- one-shot and reused parser sessions;
- source-memory and span alternate name lookup paths;
- `SmallReferenceList2<T>` and `SmallReferenceList4<T>`;
- collection-shape and retained-memory runners;
- dense, sparse, and dictionary payload indexes.

The user explicitly requested a later dedicated optimization/cleanup pass. Remove or promote variants only after the relevant comparison is complete.
