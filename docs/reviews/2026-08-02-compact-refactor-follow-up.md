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
- Removed dead Compact references to Pipelines, Streaming, and its transitive object-pool dependency.
- Moved the orphan pool documentation and corrected EOF/lifetime wording in code and samples.

## Regression coverage

The focused suite covers:

- null selectors;
- all five HTML whitespace separators and NBSP token behavior;
- deterministic descendant memoization work bounds;
- 12,000-level trees without recursive projection walkers;
- zero retained text values for attribute-only plans;
- missing versus empty values and unknown field names;
- malformed-markup topology parity with AngleSharp.

Verification on 2026-08-02:

- Compact, sample, and benchmark `net10.0` builds: zero warnings and zero errors;
- focused `EofProjection*`: 33 passed, zero failed;
- full `net10.0`: 179,304 passed, zero failed;
- `git diff --check`: clean.

## Remaining Compact work

- Make diagnostic collection opt-in internally; hiding counters removed API surface, but normal execution still records
  them.
- Preflight required fields before materializing expensive optional normalized text, and reuse identical target searches
  within a row if measurement justifies the added machinery.
- Audit the separate reusable Compact document/query/parser surface. This refactor intentionally narrows only the EOF
  projection lane and does not decide which low-level DOM configuration options deserve a public pre-alpha contract.
- Benchmark the renamed projection lane before changing its matching algorithm again. The memoized matcher is retained
  only for selector shapes that can revisit state.
