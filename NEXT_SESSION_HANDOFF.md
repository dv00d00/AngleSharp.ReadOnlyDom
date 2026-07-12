# Direct compact DOM handoff

Delete this file when the follow-up task is complete. It is deliberately a temporary handoff, not project documentation.

## Product model agreed in this session

The target is closer to `System.Text.Json.JsonDocument` than to a persistent POCO DOM:

- parse, extract, dispose is the dominant workload;
- the document owns or anchors the input source and is disposable by default;
- text and attribute values should normally remain source-backed slices/offsets and must not be copied into a final text buffer;
- pooled scratch and final storage are expected to win for sequential use with prompt disposal;
- an explicit detached/owned mode may copy text for callers that need independence from the source;
- do not optimize the default for many simultaneously retained documents at the expense of the main workload.

This changes the interpretation of `DirectCompactParser.Finalize`: its current `CopyText` pass is a control implementation, not the desired default. A source-anchored result must take ownership of the parser `TextSource` (or a purpose-built source owner) before the temporary construction document is disposed. Disposal must release both source and rented columns. Avoid retaining `StringOrMemory` objects per node in the final representation; store compact source ranges and handle decoded/entity-expanded or parser-synthesized text in a small side buffer.

## Current branch and evidence

Branch: `codex/direct-compact-construction`

- `c4154ed` adds the isolated direct compact prototype, tests, retained runner, and benchmarks.
- `6003e39` adds reusable parser sessions, nullable arena collections, four inline child slots in production ROD, and preserves the generic four-slot fixture.
- Full solution and exhaustive tests passed immediately after `6003e39`: .NET 10 166,100; net481 166,090.
- `COMPACT_DOM_DECISION.md` contains the benchmark history and architectural constraints.
- Current final compact storage is about 67 B/node, but construction allocation remains worse than ROD because of wrappers, `NodeState`, per-owner collections, and finalization bookkeeping.
- Pooling helps sequential parse/use/dispose but wastes memory when several documents remain rooted because rented bucket capacity is retained.

## Collection-shape result

Run the reproducible probe with:

```powershell
dotnet run --project AngleSharp.ReadOnlyDom.Benchmarks -f net10.0 -c Release -- --collection-shapes --tier full --output COLLECTION_SHAPES.md
```

On the checked-in 47-page corpus:

- 89.0% of nodes have zero or one child; the existing singleton representation is doing the important work.
- Of 74,088 nodes that need a child-list object, the x64 shape model estimates: inline 1 = 8.08 MB, inline 2 = 7.37 MB, inline 4 = 7.53 MB. Two is the provisional size winner; benchmark real implementations for 2 and 4 because array/object/GC effects are not fully represented by the model.
- 82.3% of elements have zero or one attribute. The named map already embeds attribute one.
- For additional attributes, the model estimates: inline 0 = 9.53 MB, inline 1 = 9.77 MB, inline 2 = 11.05 MB, inline 4 = 14.39 MB. Zero/one deserve a real implementation benchmark; the earlier nullable-`List<T>` experiment is not equivalent because `List<T>` adds another object and regressed allocation.
- A contract that emits no attributes should use no map/list at all and expose a shared empty view. It is distinct from an inline capacity of zero. Add this only as an explicit profile/option because dropping attributes changes DOM semantics.

Capacities should be owner-specific and easy to swap in the experiment. Do not introduce one generic small-list policy for children, attributes, parser stacks, and final columns.

## Next experiment order

1. Add a source-anchored final value representation and make it the direct compact default. Categorize values into direct source range versus decoded/synthesized side-buffer range; measure how often the fallback occurs on the corpus.
2. Implement real child-list capacity 2 and 4 variants behind an internal experiment switch or separate types. Measure parse time, allocated bytes, retained bytes, and overflow-array counts. Keep the singleton fast path.
3. Implement additional-attribute capacity 0 and 1 (keep current 2 as control). Separately add a no-attributes-emitted compact profile/option with a shared empty map.
4. Reduce parser wrappers: first lazily materialize leaf wrappers, then pool stable element wrappers across sequential documents. A single flyweight is unsafe because AngleSharp concurrently retains identities in open-elements and active-formatting stacks.
5. If wrappers remain dominant, prototype or propose the upstream opaque-handle tree-builder sink. Raw token consumption is last resort because it inherits adoption-agency, foster-parenting, templates, foreign content, and insertion-mode correctness.
6. Rerun malformed/property oracle tests against AngleSharp core after every construction change.

## Temporary spinoffs to evaluate, then remove or promote

- `AngleSharp.ReadOnlyDom.CompactPrototype/`: isolated architecture probe. Promote deliberately or remove as a unit; do not accidentally ship it as production API.
- `DirectCompactBenchmark.cs`, direct cases in `RetainedMemoryRunner.cs`, and `DirectCompactArenaTests.cs`: retain while choosing architecture, then migrate useful coverage/benchmarks or delete with the prototype.
- `CollectionShapeRunner.cs` and `COLLECTION_SHAPES.md`: disposable capacity-selection probe; remove after real variants settle the decision.
- `SmallReferenceList4.cs` and its layout assertion: preserved fixture for the deep optimization pass; remove if no owner selects four slots.
- `SmallReferenceList.cs`: production attribute storage today; keep until zero/one/two comparison is complete.
- owned exact arrays, pooled arrays, list-backed arena, pooled arena, one-shot parser, reused parser, source-memory name lookup, and .NET 10 span alternate lookup: benchmark controls. Remove losing paths only in the dedicated cleanup pass, not while gathering evidence.
- `COMPACT_DOM_DECISION.md`: convert durable conclusions into normal design documentation, then remove experiment chronology if it becomes misleading.

Before deleting anything, confirm no public API, test oracle, net481 compatibility path, or benchmark baseline depends on it. The user explicitly asked to preserve seemingly pointless variants until the dedicated deep-optimization cleanup.
