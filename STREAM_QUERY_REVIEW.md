# Stream Query Review

Status: open review findings after the July 18, 2026 naming pass.

Scope: the query implementation in `src/AngleSharp.ReadOnlyDom.Streaming`, the public API files in its `Public` folder, plus the tokenizer boundary, public README examples, samples, benchmarks, and focused query tests.

The engine is intentionally allocation-conscious and its compiled-plan/runtime split is sound. The most important remaining work is to make its structural contract honest: today it behaves as a lexical tag-stack query engine, not as an HTML tree query engine.

## Priority findings

### P1: optional HTML end tags break `Child` and `Descendant` queries

`QueryExecution` maintains a lexical stack of start tags and closes frames on matching end tags, explicit self-closing syntax, known void elements, or EOF. It does not apply HTML tree-construction rules or implied end tags.

This produces incorrect results for common valid HTML. For example:

```html
<ul><li>one<li>two</ul>
```

For a query equivalent to `ul.Child("li")`, only the first `li` matches. When the second `li` begins, the first remains the lexical parent even though HTML parsing implicitly closes it.

The same class of problem affects at least:

- `li`;
- `dt` and `dd`;
- `p`;
- `rt` and `rp`;
- `optgroup` and `option`;
- `thead`, `tbody`, and `tfoot`;
- `tr`;
- `td` and `th`.

There is a related divergence for self-closing syntax. HTML ignores the slash on ordinary HTML elements such as `<div/>`, while the query execution currently closes every syntactically self-closing start tag immediately.

Decision required:

1. Implement the necessary HTML scope and implied-end-tag rules, preferably using a reusable structural consumer seam from AngleSharp core; or
2. Keep lexical semantics, rename the public surface accordingly, and prominently document that `Child` and `Descendant` do not mean browser-DOM relationships.

For production extraction, the first option does not necessarily require a complete DOM tree builder. A measured minimal structural state machine covering the common optional-end-tag families may be sufficient, but its accepted subset must be explicit.

Acceptance tests:

- consecutive omitted `li` end tags both match `ul.Child("li")`;
- omitted `td` end tags produce sibling cells under the row;
- a block start tag implicitly closes an open `p` where required;
- `<div/>text` follows the selected lexical or HTML contract explicitly;
- existing malformed-table topology behavior is either preserved as an explicit lexical mode or updated intentionally.

### P2: repeated low-level handler registration silently replaces earlier handlers

`QueryNode.OnStart`, `OnText`, and `OnEnd` assign directly to one delegate field. Calling one of these methods twice silently discards the first handler. In contrast, registering a second completed-element handler throws.

Recommended fix: reject duplicate registration consistently. Multicast composition is possible, but rejection keeps callback order, allocation, and performance obvious.

Acceptance tests:

- a second `OnStart`, `OnText`, or `OnEnd` registration throws a descriptive `InvalidOperationException`;
- the existing completed-handler exclusivity tests continue to pass.

### P2: start-element attribute lookup has inconsistent casing

`Element.TryGetAttribute(string, ...)` uses `StringComparison.Ordinal`. `CompletedElement.TryGetAttributeUtf8(string, ...)` uses `OrdinalIgnoreCase`.

HTML attribute names use ASCII case-insensitive semantics. A start callback asking for `"HREF"` should behave consistently with a completed callback asking for the same spelling.

Recommended fix: normalize the requested string through the same ASCII-name normalization used by selectors, or use a dedicated ASCII-ignore-case comparison. Avoid general Unicode casing for the UTF-8 overload.

Acceptance tests:

- string lookup succeeds for lowercase, uppercase, and mixed-case ASCII spellings;
- UTF-8 lookup has a clearly documented normalized-lowercase contract;
- non-ASCII attribute-name input is rejected consistently.

### P2: projected start attributes are visible across matching query nodes

The execution creates one `Element` view over plan-global attribute arrays and passes it to every matching start handler. If two query nodes match the same start tag and request different attributes, either handler can read attributes requested only by the other node.

This does not match the per-handler meaning implied by `projectedAttributes`. `CompletedElement` already restricts its exposed indexes per compiled node.

Recommended fix: carry a per-node allowed-attribute mask into the start-element view. If plan-global visibility is intentional for performance, rename the arguments to `requestedAttributes` and document the shared visibility explicitly.

Acceptance test: two independent matching queries request different attributes, and each callback observes only the attributes promised by the selected contract.

### P2: selector-name validation accepts impossible names

`Selector.NormalizeName` rejects non-ASCII input but accepts ASCII whitespace, quotes, `=`, `/`, NUL, and control characters. Inputs such as `Selector.Tag("a b")` compile successfully but cannot match a tokenizer-produced tag name.

Recommended fix: validate the supported HTML-name grammar and reject tokenizer delimiters and control characters. If the implementation remains deliberately narrower than HTML custom-element/XML naming, document the accepted grammar.

Acceptance tests should cover valid uppercase normalization, hyphenated custom names, invalid whitespace, delimiters, control characters, empty strings, and non-ASCII input.

### P3: structural stack identity relies only on FNV-1a hash and length

Query candidate dispatch uses hash and length only as a prefilter and then confirms with `SemanticEquals`, which is safe. Structural end-tag matching and void-element recognition use only the 64-bit semantic hash and name length.

An accidental collision is very unlikely, but structural correctness should not depend on collision resistance, especially for untrusted input.

Recommended fix:

- use a canonical tag/name ID supplied by the core tokenizer; or
- retain enough stable name identity to confirm equality when closing a frame.

Void recognition can be made exact during `StartTag`, while the callback-scoped `Utf8HtmlName` is still available, and stored as a pending Boolean for `StartTagEnd`.

### P3: borrowed lifetime contracts are under-documented

`Element` and its returned attribute spans are callback-scoped, but this is not documented as clearly as `CompletedElement.TextUtf8`.

Add XML documentation covering:

- callback-only validity of borrowed spans;
- which methods allocate owned strings;
- normalized versus raw text behavior;
- attribute-name casing requirements;
- the fact that completion may be triggered by an end tag, lexical recovery, void handling, or EOF.

## Remaining API and naming work

The following public renames were deliberately deferred because they require a coordinated edit of the delegates and factories currently grouped in `Common.cs`:

| Current | Proposed | Reason |
|---|---|---|
| `QueryNode<TState>` | `QueryPattern<TState>` or `QueryBuilderNode<TState>` | It is a mutable query definition, not an input node. |
| `Element` | `StartElementView` | Avoid confusion with AngleSharp DOM elements and expose its callback-scoped nature. |
| `CompletedElement` | `CapturedElement` | Completion can occur through EOF or lexical recovery, not only a real HTML close. |
| `ResolvedQueryPlan<TState, TResult>` | `ProjectedQueryPlan<TState, TResult>` | The object is not resolved until execution. |

Suggested callback vocabulary for a later public API pass:

- `OnStart` -> `OnStartTag`;
- `OnText` -> `OnTextChunk`;
- `OnEnd` -> `OnScopeClosed`;
- `OnClose` -> `OnCaptured` or `OnElementCaptured`;
- `OnTextContent` -> `OnRawText`.

`Id`, `Class`, and `Attribute` are the selected `QueryNode` fluent vocabulary. The duplicate selector object vocabulary is now internal.

## Naming changes already applied

Do not repeat these in the next pass:

- `CompiledQueryNode` -> `QueryPlanNode`;
- `ElementCapture` -> `CapturedElementBuffer`;
- `QueryCompiler` -> `QueryPlanCompiler`;
- `QuerySession` -> `QueryExecution`;
- `QueryPlan.CreateSession` -> `CreateExecution`;
- `VoidElementHashes` -> `HtmlVoidElements`;
- `TagName` -> `TagNameUtf8` in compiled plan nodes;
- bitset members now use the `Mask` suffix;
- `CompletedAttributeIndexes` -> `CapturedAttributeIndexes`;
- `QueryExplanation.ExecutionShape` string -> `QueryExecutionModel ExecutionModel`;
- execution model value -> `QueryExecutionModel.LexicalStreaming`;
- unused `QueryExplanation.FailureReason` removed.
- `QueryExecution`, `Selector`, `QueryRelation`, and `NormalizedUtf8Writer` made internal;
- `QueryPlan.CreateExecution` made internal;
- `QueryNode.Selector`, `Relation`, `Parent`, selector overloads, and static `Root` factories made internal;
- `QuerySet` removed; `StreamQuery.Observe` now returns a compiled `QueryPlan` directly;
- `QueryExplanation.CanStopAfterRoot` removed and construction made internal;
- `ICommittedUtf8Output` -> `IUtf8PublishSource`;
- `CommittedUtf8Buffer` -> `PublishableUtf8Buffer`;
- `Commit` vocabulary replaced with `MarkPublishable`, `PublishableUtf8`, and `AdvancePublished`;
- direct `PipeWriter` execution overloads added to `QueryPlan`, and `HtmlTextExtractor` no longer uses an intermediate publish buffer.

The public delegate and execution-model declarations remain in `Public/Common.cs`; the internal query relation no longer leaks through the API.

## Lower-priority implementation/style improvements

- Split `QueryExecution` only along real responsibilities: compiled matching/structural stack versus completed-element capture accounting. Avoid adding interfaces to hot paths without measurement.
- Consider a smaller initial frame-pool rent than 64 entries, or cap the initial rent by `MaximumNestingDepth`; verify with allocation benchmarks.
- Reject null `params` arrays explicitly in handler registration rather than allowing a `NullReferenceException` during enumeration.
- Keep `CompiledTagDispatch` out of a miscellaneous `Public/Common.cs` bucket when that file is reorganized. It is internal and belongs in `Common.Internal.cs` or a small `QueryPlanModels.cs` file.
- `QueryPlanCompiler` is internal, so its compile methods are now internal as well.

## Verification checkpoint

After the naming changes:

- `AngleSharp.ReadOnlyDom.Streaming` built successfully for `net8.0` with zero warnings;
- all `net10.0` consumers compiled, including tests, samples, Markdown proxy, and benchmarks;
- focused query tests passed: 10/10;
- streaming outcome tests passed: 6/6;
- backpressure tests passed: 2/2;
- streaming limit tests passed: 13/13.

After the public-surface reduction:

- `AngleSharp.ReadOnlyDom.Streaming` builds for `net8.0` and `net10.0` with zero warnings;
- tests, samples, Markdown proxy, and benchmarks compile;
- focused streaming/query tests pass: 28/28;
- the complete `net10.0` test suite passes: 179,277/179,277;
- direct `PipeWriter` backpressure is covered for UTF-8 and transcoded input.

The aggregate `--no-restore` solution build still encounters missing assets for unrelated `net472`, `net8.0`, and `netstandard2.0` targets. Those failures are restore-state issues rather than query compilation failures.
