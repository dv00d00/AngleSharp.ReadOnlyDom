# Streaming refactor follow-up

Date: 2026-08-03
Starting review: [Pull request #56: Streaming lane review](2026-08-02-pr-56-streaming.md)

## End state

`AngleSharp.ReadOnlyDom.Streaming` remains one focused library: native UTF-8 tokenization plus bounded lexical queries,
captures, rewrites, and backpressured output. It no longer exposes opinionated text extraction, duplicated resolved-plan
execution overloads, or compile-time explanation objects without a production consumer.

The structural contract is deliberately lexical. The implementation does not contain a partial optional-end-tag state
machine. [The stable contract](../STREAMING_QUERY_CONTRACT.md), public XML documentation, samples, and tests now state
that `Child` and `Descendant` do not mean browser-corrected DOM relationships. Corrected topology remains issue #42.

## Correctness fixes

- Backpressured UTF-8 and transcoded execution now publishes from `execution.State`, including when a callback replaces
  reference-type state through `ref`.
- Start callbacks see only attributes projected by their own query node. A rewrite terminal sees the union required by
  matching terminal nodes, not unrelated plan-global attributes.
- String attribute lookup is case-insensitive for HTML ASCII names; UTF-8 span lookup retains its normalized-lowercase
  contract.
- Repeated start, text, and end handler registration is rejected consistently instead of silently replacing delegates.
- Null projected-attribute arrays are rejected at the public boundary before any query mutation.
- Tag and attribute names reject control bytes and tokenizer delimiters. Attribute validation separately rejects `=`;
  tag validation retains it because the tokenizer can represent it in a parse-error tag name.
- Non-compact structural frame names use hash and length only as a prefilter and then confirm semantic byte equality.
  Fallback name buffers are pooled, released during normal closure and EOF, and released safely on disposal or callbacks
  that throw.
- Initial frame-pool rent respects a lower configured nesting limit rather than always requesting 64 entries.

## Public-surface reduction

Removed:

- `HtmlTextExtractor` and `HtmlTextOptions`: opinionated lexical HTML-to-text policy with no library consumer;
- `ResolvedQueryPlan<TState, TResult>` and `QueryPlan.Resolve`: a wrapper duplicating every execution overload;
- `QueryExplanation` and `QueryExecutionModel`: diagnostics with only a test consumer that imposed compile-time work on
  every plan.

Outcome tests now execute a normal `QueryPlan<TState>` and explicitly resolve the returned caller-owned state.
The former outcome sample was replaced by a smaller end-to-end example: it turns an HTML resource page from a
`PipeReader` into an NDJSON content feed, writes borrowed attribute and text spans through a per-record
`Utf8JsonWriter`, and publishes each completed record to a backpressured `PipeWriter` without retaining a DOM or
intermediate content model. Buffering is bounded to the current record. Extractor-specific tests were deleted;
independent publish-buffer and whitespace-normalization contracts were preserved as `StreamingOutputTests`.

The opinionated behavior was subsequently restored in `AngleSharp.ReadOnlyDom.ExtractionExamples`, not in the public
library: one example builds normalized text directly from `StreamQuery`, another walks a corrected Compact DOM, and a
sample-local Compact Markdown projection adapts the former writer to public node cursors.

## Sample boundary and security

The Markdown project is now an example of one conversion only:

- `POST /markdown` accepts caller-provided HTML and streams Markdown output;
- input is bounded to 4 MiB, including chunked requests through `HtmlStreamingLimits`;
- remote URL fetching, redirects, asset proxying, plain-text mode, and custom preview rendering were removed;
- the browser UI writes only textarea values and never executes generated Markdown links as HTML.

The sample README and initial input use explicit `<html><body>` tags because the query plan follows lexical input rather
than synthesizing browser document structure.

Markdown navigation was restored as the separate `AngleSharp.ReadOnlyDom.MarkdownNavigation` project. Every transition
streams a checked-in HTML fixture through the shared `QueryPlan<MarkdownBuffer>` before rendering the resulting Markdown.
The endpoint accepts only a fixed page map, constructs preview nodes without `innerHTML`, and does not restore remote
fetch or asset-proxy behavior.

## Removed maintenance artifacts

- Deleted the root `STREAM_QUERY_REVIEW.md`; stable decisions now live in the contract and tests, while this review
  directory retains the dated audit trail.
- Deleted the older count-only `Utf8TokenizerBenchmark`. It compared different token operations without validating
  equivalent output. The fingerprinted contiguous/segmented baseline is now included in the maintained `utf8` tier.

## Issue disposition

- #42 remains the high-priority corrected-topology boundary.
- #50 remains blocked by #42; new public lexical filter combinators would expand the wrong contract.
- #51 should be reassessed after #42. Bounded callbacks and caller-owned state exist now; JSON and typed outcome policy
  remain examples unless a smaller missing library primitive is demonstrated.

No GitHub issue was edited or closed during this refactor.

## Verification

The focused slices were verified independently before the final aggregate run:

- Streaming library builds for `net8.0` and `net10.0` with zero warnings and errors;
- core query, backpressure, outcome, output, and streaming-limit tests pass;
- the general samples and the simplified Markdown sample build;
- the Markdown endpoint was exercised over loopback, and removed GET/proxy routes return 405/404;
- `git diff --check` is clean.

The final solution build and complete `net10.0` test count are recorded in the implementation handoff rather than frozen
here, because counts naturally change as unrelated tests are added.
