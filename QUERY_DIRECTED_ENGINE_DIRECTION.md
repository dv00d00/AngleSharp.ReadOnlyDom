# Query-directed processing direction

This note preserves the product direction discovered while finishing the indexed compact DOM experiment. It is intentionally not part of issue #6's implementation scope.

## Product thesis

The larger opportunity is not only a cheaper DOM. It is a compiled extraction engine that makes the parser produce the minimum artifact required by a reusable query.

Three execution families should share tokenizer and HTML tree-construction semantics:

1. Streaming extraction into a result sink when bounded parser state is sufficient.
2. Forward preorder scan over a compact tree when subtree completion or richer structural matching is required.
3. Compact read-only materialization when handles, repeated unknown queries, navigation, diagnostics, or source inspection must outlive parsing.

The compact DOM is the fallback representation and an optimization of the existing read-only DOM. It must not be forced to carry the future query language.

## Non-negotiable correctness boundary

Query-directed output does not imply that irrelevant input can be skipped blindly. Malformed formatting, foster parenting, active formatting elements, templates, foreign content, and insertion modes can have nonlocal effects.

The safe default is:

> Run all tokenizer and tree-builder state required for correct HTML semantics; retain, decode, index, or materialize only what the execution plan requires.

True tokenizer skipping and early termination need state-specific proof and differential tests against AngleSharp core.

## Lifetime and text model

The intended owning parser resembles `System.Text.Json.JsonDocument`:

- parse, extract, dispose is the primary workload;
- the document owns or anchors its input;
- text and attribute values normally remain source slices;
- decoded entities and parser-synthesized values use a compact side arena;
- pooled columns and scratch are released by document disposal;
- escaping results explicitly copy or transfer ownership.

A compact document built from an already materialized read-only DOM cannot silently take ownership of that DOM's input. Its source-backed values are borrowed. The direct parser is the correct place to implement an independently owning lifetime.

## Feature planning

Inspection, retention, and indexing are different costs. A plan may inspect `id` and `class`, retain only `href`, and build no general attribute collection.

Optional immutable payloads should be selectable at document/plan level. Candidate storage modes are:

- dense arrays for frequent payloads and hot random access;
- sorted sparse handle/value arrays for rare payloads and sequential or infrequent lookup;
- dictionaries for sparse, frequent random lookup where measurement justifies their overhead;
- no storage when the feature is disabled.

Keep storage-mode branches outside hot loops by selecting specialized execution paths once per document or plan.

## First decisive product experiment

Implement one representative query end to end:

```text
first div#content -> normalized subtree text
```

Compare:

1. current read-only DOM plus traversal;
2. compact preorder document plus forward scan;
3. tree-builder-integrated extraction with no retained DOM;
4. a hand-written streaming baseline.

Measure total time, allocation, retained/peak memory, bytes consumed before termination, tokens processed, values decoded, nodes materialized, and output allocation. This establishes whether materialization avoidance is worth the much larger architecture.

## Scope discipline

Start with tag, id, class token, attribute equality, descendant/direct-child paths, first/all cardinality, attribute projection, and normalized subtree text. Use an inspectable interpreted plan. Do not begin with a full CSS language, generic optimizer, generated IL, SIMD selectors, bitmap rank/select storage, or a universal column framework.

Issue #7 remains the separate semantic text-projection investigation. The structured query engine may share lower-level machinery later, but neither should be forced into the other's public API before measurements.
