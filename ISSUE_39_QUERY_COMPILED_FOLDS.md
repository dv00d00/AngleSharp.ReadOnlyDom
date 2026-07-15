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
| Low-level compiled fold | 1.217 ms | 16.30 KB |
| Completed-element fold | 1.237 ms | 20.71 KB |

The ShortRun mean difference was 1.6%. The additional 4.4 KB owns per-match normalized text and decoded attributes until
the element closes. Plans that do not use completed-element callbacks do not allocate capture storage.
