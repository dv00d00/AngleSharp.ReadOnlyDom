# Compact projection refactor follow-up

This follow-up records the implementation performed after the pull request #56 Compact review. The original review
remains a snapshot of commit `4e86c68342b1295a7f2c3ecb5ac878514ef97493`; its old symbol names are intentionally not
rewritten.

## Library boundary

`AngleSharp.ReadOnlyDom.Compact` owns the HTML-aware projection mechanism:

- EOF evaluation over AngleSharp's constructed topology;
- `First` and `ForEach` scope selection;
- child and descendant selector paths with id, class-token, and attribute predicates;
- owned attribute and explicitly named normalized-subtree-text projections;
- owned rows and values with distinct missing, empty, and unknown-field behavior;
- internal retention requirements and execution counters used by implementation, tests, and benchmarks.

Product and output policy is not part of that contract. JSON serialization is now a sample-local helper. Markdown
conversion, article/search-result schemas, HTTP fetching, redirects, asset proxying, and UI behavior belong in samples or
applications. The projection library consumes HTML and has no URL, HTTP, Markdown, or JSON dependency.

## Completed cleanup

- Renamed the pre-alpha lane consistently from `CompactAggregate*` to `CompactProjection*`; the field descriptor is
  `CompactFieldProjection`.
- Hid cardinality, selector steps, projection representation, retention requirements, and execution counters from the
  public API.
- Removed production `Explain`, `ToJson`, and `WriteJson`; retained JSON only in the console sample.
- Fixed null selector handling and exact HTML ASCII-whitespace class-token semantics.
- Restricted descendant-state memoization to selector shapes with two or more real descendant axes, where repeated
  `(node, step)` states can occur.
- Replaced recursive scope, descendant-target, and normalized-text walks with an iterative preorder traversal.
- Stopped attribute-only plans from retaining or decoding text payloads.
- Made unknown projected field names throw instead of silently looking identical to a known field whose value is absent.
- Made diagnostic collection opt-in through a friend-only execution path. Normal `Execute` no longer installs tokenizer
  middleware, counts tokens/nodes, or computes consumed UTF-8 bytes; its diagnostic result is the default value.
- Compiled required fields ahead of optional fields and compiled equivalent field selectors to shared target slots, while
  preserving caller field order in emitted rows.
- Removed dead Compact references to Pipelines, Streaming, and its transitive object-pool dependency.
- Moved the orphan pool documentation and corrected EOF/lifetime wording in code and samples.
- Reduced the reusable Compact DOM surface to document lifetime/count/metadata, node navigation and string/span
  attr/class/text access, string/span element queries, and string/character-memory/byte-memory/async-stream parser inputs.
- Internalized raw storage structs and accessors, numeric name IDs and handles, layout/capacity hints, generic sinks,
  tokenizer middleware, attribute filters, and low-level `TextSource`/array paths.
- Removed the opinionated `CompactParserProfiles.Extraction` preset; explicit `HtmlParserOptions` remain available.
- Moved source-location lookup onto `Node`, so the public API no longer required an inaccessible raw handle.

## Regression coverage

The focused suite covers:

- null selectors;
- all five HTML whitespace separators and NBSP token behavior;
- deterministic descendant memoization work bounds;
- 12,000-level trees without recursive projection walkers;
- zero retained text values for attribute-only plans;
- no diagnostic collection on normal execution;
- required-field preflight before optional normalized-text materialization;
- one subtree target scan for equivalent field selectors;
- missing versus empty values and unknown field names;
- malformed-markup topology parity with AngleSharp.

Verification on 2026-08-02:

- full solution build including net472, net8.0, and net10.0: zero errors (three existing TUnit analyzer warnings);
- Compact, sample, and benchmark builds: zero warnings and zero errors;
- focused `EofProjection*`: 36 passed, zero failed;
- focused `CompactParserTests`: 83 passed, zero failed;
- full `net10.0`: 179,306 passed, zero failed;
- `git diff --check`: clean.

## Follow-up disposition

All concrete follow-ups from the initial review are implemented. The descendant matcher itself was not changed again:
memoization remains restricted to selector shapes that can revisit state, and any future replacement remains explicitly
measurement-gated rather than an outstanding cleanup task. Larger roadmap work is tracked separately in the open-issues
audit.
