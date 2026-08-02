# Streaming query contract

`AngleSharp.ReadOnlyDom.Streaming` is the bounded, native UTF-8 lane for workloads whose result shape is known before
parsing and where no DOM needs to escape. It owns tokenization, compiled lexical queries, bounded captures, start-tag
rewriting, and backpressured execution. Opinionated conversions, HTTP endpoints, and user interfaces belong in samples.

## Structural semantics

Query structure follows the lexical start/end-tag stream:

- `Child` means the immediately enclosing active lexical frame.
- `Descendant` means any active lexical ancestor frame.
- an explicit matching end tag closes the matching frame and any still-open inner frames;
- syntactically self-closing tags, known HTML void tags, and end of input close frames directly.

This is not browser-corrected HTML tree topology. The engine does not apply implied end tags, foster parenting, the
adoption agency algorithm, or browser treatment of a slash on ordinary HTML elements. For example, consecutive `<li>`
start tags without `</li>` and malformed table content can produce different relationships from an AngleSharp DOM.

Use the object or Compact retained-DOM lane when corrected HTML-tree relationships are part of the result. Corrected
streaming topology remains tracked by GitHub issue #42; no partial optional-end-tag heuristic is part of this contract.

## Ownership and limits

- `Element`, `CompletedElement`, and their UTF-8 spans borrow execution buffers and are valid only during the callback.
- `GetText` and `GetAttribute` explicitly allocate owned UTF-16 strings.
- only attributes projected by a query node are visible to its callback.
- string attribute lookup is case-insensitive for HTML ASCII names; span lookup uses the normalized lowercase UTF-8 name.
- input bytes, buffered token bytes, lexical nesting depth, and query captures are bounded by `HtmlStreamingLimits`.

## Observations and conversion examples

`StreamQuery.Observe` compiles independent roots into one pass over shared caller-owned state. Execution returns that
state; application-specific resolution happens explicitly afterward. The samples keep outcome policy and Markdown
conversion outside the library so those examples remain useful without becoming permanent public abstractions.
