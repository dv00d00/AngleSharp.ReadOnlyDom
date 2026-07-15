# Issue #39: query-compiled UTF-8 folds

## Implemented prototype

`Utf8StreamQueryNode<TState>` builds a small C# query tree and compiles it into a reusable
`Utf8StreamQueryPlan<TState>`. The plan consumes either contiguous UTF-8 or a `PipeReader` and sends
borrowed start, text, and end callbacks into caller-owned state.

The implemented structural subset is deliberately small:

- tag selectors;
- ID, class-token, attribute-existence, and attribute-equality predicates;
- child and descendant relationships;
- explicitly projected attributes;
- synchronous start, text, and end handlers.

Compilation pre-encodes literals, hashes tags, confirms candidate bytes after hash matches, assigns
attribute IDs, and derives per-tag candidate and required-attribute bitsets. The parser therefore does
not copy or compare query attributes on tags that cannot structurally match. Attribute values required
by predicates or callbacks are copied only into reusable pooled scratch because tokenizer spans expire
before the complete start-tag callback. Text remains borrowed and may arrive in any number of chunks.

The current explanation reports `StreamingOnly`, required tags and attributes, query-node count,
estimated per-frame bytes, and that early termination is not yet available.

## Explicit limits

- At most 64 query nodes and 64 required attributes; compilation rejects larger plans.
- ASCII tag and attribute names only. Attribute values and text remain UTF-8.
- Lexical open-element nesting, not browser tree-construction equivalence.
- No siblings, positional selectors, `:has`, arbitrary CSS, asynchronous callbacks, or implicit
  materialization fallback.
- No first/singular root or early termination yet.
- Borrowed attribute and text spans expire when their callback returns.

Malformed table content is an intentional differential test: the streaming query sees a `div` nested
lexically inside `table`, while AngleSharp foster-parents it outside the table. Consumers requiring
browser-equivalent malformed topology must use a tree-builder lane.

## QQ differential workload

The compiled query expresses the same extraction as the hand-written native sink:

```text
ul.news-list
  descendant li[dt-eid='em_item_article'] -> dt-params
    descendant a[href] -> href + subtree text
      descendant img -> src + alt
```

Both lanes produce the same ordered 16 owned `Article` records as mutable AngleSharp for the checked-in
124,794-byte `qq.html` fixture.

MediumRun (15 iterations, 2 launches), Server GC, .NET 10.0.10:

| Lane | Mean | Allocated |
|---|---:|---:|
| Hand-written native UTF-8 fold | 1.262 ms | 17.14 KB |
| Query-compiled UTF-8 fold | 1.253 ms | 16.24 KB |

The 0.7% mean-time difference is below the noise floor: the compiled query has reached the hand-written
runtime ceiling on this workload. It allocates about 0.9 KB less because values are decoded only after
the complete structural and predicate match. Owned result objects dominate both allocation totals.

## Next decisions

1. Profile before making further matcher optimizations; QQ shows no remaining abstraction tax.
2. Decide whether first/singular roots justify a tokenizer stop signal.
3. Add explicit malformed-input policy only if users need automatic rejection instead of documented
   lexical semantics.
4. Keep full browser semantics in the AngleSharp tree-builder lane.

## Ergonomic completed-element layer

The low-level borrowed start/text/end handlers remain the zero-overhead escape hatch. A fluent layer now covers the common
case where user state only changes after an element is complete:

- `StreamQuery.For<TState>("tag")` and string-based `Child` / `Descendant` paths;
- concise `Id`, `Class`, and `Attribute` predicates;
- `OnClose` for attributes only;
- `OnTextContent` for exact subtree text;
- `OnNormalizedText` for whitespace-normalized subtree text.

Predicate attributes are projected automatically. Optional attributes are named only when registering the completed
callback. Nested matches retain independent captures, and outer matches include descendant text.

On the QQ extraction workload, the ergonomic completed-element lane returned the same 16 articles as the low-level fold:

| Lane | Mean | Allocated |
|---|---:|---:|
| Low-level compiled fold | 1.222 ms | 16.26 KB |
| Completed-element fold | 1.393 ms | 16.18 KB |

Completed captures now normalize and retain UTF-8 in reusable pooled buffers. `TextUtf8` and `TryGetAttributeUtf8` expose
callback-scoped borrowed spans; `GetText` and `GetAttribute` decode owned strings only when requested. This removed the
previous 4.4 KB ownership premium. The ShortRun timing difference is about 14% on this small-result workload and remains
the cost of completed-capture bookkeeping rather than string allocation.

On the 1.96 MB target-near-EOF workload, completed-element allocation fell from 5.49 KB to 3.05 KB versus 8.01 KB for the
raw fold. The measured means were 18.60 ms completed versus 18.01 ms raw, a noise-sized difference for ShortRun.

## Possible lexical session view

`IUtf8HtmlTokenSink` already permits arbitrary user folds, but callers must maintain their own parent stack. The query
session has a reusable lexical frame stack, although each frame currently stores only tag hash, tag length, and query
match bits. It is not a read-only DOM arena: it has no retained tag spelling, attributes, sibling state, or browser-corrected
tree topology.

A future public seam should therefore be an explicitly lexical callback-scoped path or cursor, not exposure of
`QuerySession` internals. Before adding it, measure the cost of retaining a stable tag ID or tag bytes for every open frame.
Do not imply sibling lookup or HTML tree-builder semantics unless a separate construction layer provides them.

## Backpressured output prototype

`ExecuteBackpressuredAsync` now keeps the parser hot path synchronous while placing `FlushAsync` in the outer pump. Query
state implements `ICommittedUtf8Output` to expose only an irrevocable contiguous prefix. The pump tokenizes bounded input
slices, copies committed bytes to a `PipeWriter`, advances the state prefix, awaits downstream capacity, and resumes the
same tokenizer and query session.

The state, rather than the pump, owns semantic commit decisions. Token-ordered rewrites may commit immediately;
completed-subtree folds commit at close; queries depending on future structure may retain a tentative tail. A slow-reader
test with a 32-byte flush threshold and 64-byte pipe pause threshold verifies that parsing stops under downstream pressure,
resumes without changing output, and keeps producer-side committed bytes bounded.
