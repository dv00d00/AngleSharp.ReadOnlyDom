# Pull request #56: Streaming lane review

Reviewed commit: `4e86c68342b1295a7f2c3ecb5ac878514ef97493`

Scope:

- `src/AngleSharp.ReadOnlyDom.Streaming`;
- streaming tests and benchmarks;
- `samples/AngleSharp.ReadOnlyDom.MarkdownProxy`;
- the existing root-level `STREAM_QUERY_REVIEW.md`.

The substantive commits in pull request #56 do not change Streaming or MarkdownProxy behavior. Commit `4e86c68` only
reformats these files. The findings below describe the current head and are mostly pre-existing.

## P1: structural relations are lexical, not HTML-tree relations

Locations:

- `src/AngleSharp.ReadOnlyDom.Streaming/Internal/QueryExecution.cs:180`
- `src/AngleSharp.ReadOnlyDom.Streaming/Internal/QueryExecution.cs:220`
- `src/AngleSharp.ReadOnlyDom.Streaming/Internal/QueryExecution.cs:247`
- `src/AngleSharp.ReadOnlyDom.Streaming/Internal/QueryExecution.cs:323`

The runtime maintains a lexical start/end-tag stack. It does not apply HTML tree-construction rules, implied end tags,
foster parenting, or the HTML rule that ignores self-closing syntax on ordinary HTML elements.

Consequences include:

- `<ul><li>one<li>two</ul>` does not produce two sibling `li` children;
- omitted `p`, `td`, `th`, `tr`, `option`, and similar end tags distort `Child` and `Descendant` matching;
- `<div/>text` closes `div` lexically although HTML does not;
- the Markdown table query in `samples/AngleSharp.ReadOnlyDom.MarkdownProxy/MD/MarkdownPlan.cs:56` can merge or lose cells.

Recommended end state: choose and state one honest contract before alpha.

1. Preferred: consume HTML tree-construction events while retaining no DOM, so `Child` and `Descendant` mean browser HTML
   relationships.
2. Alternative: keep lexical processing, rename the relations and public types accordingly, and remove DOM/HTML-tree
   claims from high-level converters.

A partial collection of optional-end-tag heuristics is likely to become a second incomplete tree builder and is not a
clean long-term boundary.

Required tests include omitted `li`, `p`, table section/cell tags, ordinary self-closing syntax, foster parenting, and
formatting adoption.

## P1: backpressured execution publishes stale state

Locations:

- `src/AngleSharp.ReadOnlyDom.Streaming/Public/Common.cs:15`
- `src/AngleSharp.ReadOnlyDom.Streaming/Public/BackpressuredQueryExecution.cs:39`
- `src/AngleSharp.ReadOnlyDom.Streaming/Public/BackpressuredQueryExecution.cs:88`
- `src/AngleSharp.ReadOnlyDom.Streaming/Public/BackpressuredQueryExecution.cs:105`

Callbacks receive `TState` by `ref` and may replace a reference-type state object. Backpressured execution continues
checking and publishing from the original captured `state`, while the execution returns the replacement state.

Recommended end state:

- always obtain the current state from `execution.State` before checking or publishing;
- or remove state replacement from the callback contract if mutation-only state is intended;
- add a regression test that replaces an `IUtf8PublishSource` instance from a callback.

## P1: Markdown preview permits executable links

Locations:

- `samples/AngleSharp.ReadOnlyDom.MarkdownProxy/wwwroot/app.js:63`
- `samples/AngleSharp.ReadOnlyDom.MarkdownProxy/wwwroot/app.js:195`
- `samples/AngleSharp.ReadOnlyDom.MarkdownProxy/MD/MarkdownBuffer.cs:162`

The browser renderer constructs HTML with regular-expression substitutions. A Markdown link containing a `javascript:`
URL becomes an executable same-origin link. The click handler returns for unsupported schemes without calling
`preventDefault()`.

Recommended end state:

- simplest: remove the custom renderer and display the generated Markdown as text;
- otherwise, build output with DOM nodes and `textContent`, parse URLs explicitly, and always prevent navigation for
  unsupported schemes;
- test `javascript:`, `data:`, malformed, relative, HTTP, and HTTPS URLs.

## P1: the sample is an open SSRF and unbounded asset proxy

Locations:

- `samples/AngleSharp.ReadOnlyDom.MarkdownProxy/Program.cs:39`
- `samples/AngleSharp.ReadOnlyDom.MarkdownProxy/Program.cs:115`
- `samples/AngleSharp.ReadOnlyDom.MarkdownProxy/Program.cs:184`
- `samples/AngleSharp.ReadOnlyDom.MarkdownProxy/Program.cs:229`
- `samples/AngleSharp.ReadOnlyDom.MarkdownProxy/README.md:35`

The sample accepts arbitrary HTTP(S) URLs and redirect targets, including loopback, private, and link-local addresses.
The 20 MB asset check applies only when `Content-Length` is known; chunked content is copied without a byte limit.

The README warning is useful but does not make copied sample code safe.

Recommended cleanup:

- remove remote fetching, redirects, and `/asset` from the primary sample;
- retain POST/body conversion as the focused demonstration of the streaming library;
- if a proxy example remains, isolate it as an explicitly unsafe demo and implement DNS/IP checks on every redirect,
  bounded copying, timeouts/cancellation, and a strict media allowlist.

## P2: start callbacks see other nodes' projected attributes

Locations:

- `src/AngleSharp.ReadOnlyDom.Streaming/Internal/QueryExecution.cs:90`
- `src/AngleSharp.ReadOnlyDom.Streaming/Internal/QueryExecution.cs:190`
- `src/AngleSharp.ReadOnlyDom.Streaming/Public/Element.cs:25`

Requested attributes are combined into plan-global arrays, and one shared start-element view is passed to each matching
query node. A handler can therefore read attributes requested only by another query node. `CompletedElement` already
restricts visible indexes per compiled node.

Recommended end state: pass a per-node allowed mask or index list into the start-element view. If global visibility is
intentional, rename the API and document the leakage explicitly; scoped visibility is the cleaner contract.

## P2: callback registration and casing are inconsistent

Locations:

- `src/AngleSharp.ReadOnlyDom.Streaming/Public/Element.cs:22`
- `src/AngleSharp.ReadOnlyDom.Streaming/Public/CompletedElement.cs:31`
- `src/AngleSharp.ReadOnlyDom.Streaming/Public/QueryNode.cs:71`
- `src/AngleSharp.ReadOnlyDom.Streaming/Public/QueryNode.cs:130`

Start-element string lookup uses ordinal case-sensitive comparison, while completed-element lookup is case-insensitive.
`OnStart`, `OnText`, and `OnEnd` silently replace an earlier handler, while registering a second completed handler throws.

Recommended end state:

- use one documented ASCII case-insensitive string-name contract;
- reject a second handler registration consistently with a descriptive `InvalidOperationException`;
- add tests for lowercase, uppercase, and mixed-case names and every duplicate handler type.

## P2: public surface contains unproven convenience layers

Locations:

- `src/AngleSharp.ReadOnlyDom.Streaming/Public/HtmlTextExtractor.cs:7`
- `src/AngleSharp.ReadOnlyDom.Streaming/Public/ResolvedQueryPlan.cs:9`
- `src/AngleSharp.ReadOnlyDom.Streaming/Public/QueryExplanation.cs:3`
- `src/AngleSharp.ReadOnlyDom.Streaming/Public/Common.cs:10`

Candidates for removal or relocation:

- `HtmlTextExtractor` is an opinionated HTML-to-text heuristic built on lexical topology and used only by tests and the
  Markdown sample;
- `ResolvedQueryPlan` duplicates execution overloads mainly to invoke a resolver after EOF;
- `QueryExplanation` and the one-value `QueryExecutionModel` enum have no demonstrated external consumer.

Recommended end state:

- keep the tokenizer, compiled query, execution, bounded input, and backpressure contracts in the library;
- move opinionated text conversion to a sample or optional helper package;
- keep diagnostics internal until an external consumer requires a stable representation;
- avoid a wrapper type whose only purpose is to duplicate every execution overload.

The Markdown project should demonstrate Markdown conversion only. Plain-text conversion, the browser UI, remote fetch,
asset proxy, and two output modes currently obscure the core streaming example.

## P3: selector and parameter validation is incomplete

Locations:

- `src/AngleSharp.ReadOnlyDom.Streaming/Internal/Selector.cs:43`
- `src/AngleSharp.ReadOnlyDom.Streaming/Public/QueryNode.cs:75`
- `src/AngleSharp.ReadOnlyDom.Streaming/Public/QueryNode.cs:134`

Selector normalization accepts whitespace, `/`, `>`, NUL, and control characters that the supported matching contract
cannot use meaningfully. A `null` `params projectedAttributes` array can fail later with `NullReferenceException`.

Recommended end state:

- validate against the actual tokenizer/query contract, not an invented XML or HTML authoring grammar;
- reject delimiters and control input the matcher cannot represent;
- check `params` arrays explicitly and report the public parameter name.

## P3: structural fallback identity trusts hash and length

Location: `src/AngleSharp.ReadOnlyDom.Streaming/Internal/QueryExecution.cs:247`

Candidate dispatch confirms name equality after hash prefiltering, but structural end-tag matching for non-compact names
uses only FNV hash and length. Correctness for untrusted input should not depend solely on collision resistance.

Recommended end state: retain a canonical name identity or enough stable bytes to confirm equality when closing a frame.

## P3: an older tokenizer benchmark no longer supplies useful evidence

Location: `benchmarks/AngleSharp.ReadOnlyDom.Benchmarks/Suites/Utf8/Utf8TokenizerBenchmark.cs:33`

The benchmark compares different operations/counts and performs no output validation. The fingerprint-based
`Utf8TokenizerBaselineBenchmark` and smoke/conformance tests provide a better maintained gate.

Recommended end state: delete the older benchmark after confirming it has no unique workload.

## Root review artifact

`STREAM_QUERY_REVIEW.md` is a stale root-level review journal. Several findings remain valid, while its verification
counts and naming history naturally decay.

Recommended end state:

- encode resolved behavior in tests;
- move the stable lexical-versus-HTML decision into a design/contract document;
- track unresolved implementation work as issues or the current review snapshot;
- delete `STREAM_QUERY_REVIEW.md` rather than maintaining two review sources.

## Recommended implementation order

1. Fix stale-state publication in backpressured execution.
2. Remove executable-link and SSRF behavior from the sample.
3. Decide lexical versus HTML-tree semantics and align names, docs, and tests.
4. Scope projected attributes and make callback/casing behavior consistent.
5. Reduce public convenience and diagnostic surface.
6. Tighten validation and structural identity.
7. Remove the obsolete benchmark and stale review journal.
