# Issue 14: preorder subtree boundaries

## Decision

The final compact representation stores `SubtreeEndExclusive` in the second 32-bit field of the 16-byte
`CompactNode` core. Mutable construction continues to use first-child/next-sibling links. Finalization converts those
links into preorder subtree boundaries for both packed and frozen layouts.

This is the default representation, not an opt-in profile. It does not widen the node, add a sidecar, or measurably
increase allocation or retained memory. Keeping a selectable sibling-link representation would add storage and query
branches without a measured workload that benefits from it.

The boundary enables:

- constant-time subtree skipping and same-document descendant checks;
- allocation-free descendant iteration over a bounded preorder range;
- bounded tag scans, including the existing SIMD name-ID scan in frozen layout;
- direct-child iteration by jumping from each child to its subtree end.

Template contents retain their separate traversal boundary and are not accidentally included in their host template's
ordinary descendant range.

## Server-GC benchmark evidence

All benchmark entry points require Server GC. BenchmarkDotNet adds an explicit server-GC job, the benchmark executable
fails fast when Server GC is unavailable, and the project runtime configuration enables Server GC for the manual corpus,
query, and retained-memory runners.

The authoritative construction comparison is BenchmarkDotNet on .NET 10 with Concurrent Server GC. Old is the
first-child/next-sibling final core; new is first-child/subtree-end. Values are mean time and allocated bytes per operation.

| Workload | Old | New | Change | Old allocation | New allocation |
| --- | ---: | ---: | ---: | ---: | ---: |
| Fresh compact parser | 378.5 us | 374.1 us | -1.2% | 31.52 KB | 31.52 KB |
| Reused compact parser | 375.5 us | 383.0 us | +2.0% | 31.42 KB | 31.42 KB |
| Reused parser, no attributes | 333.8 us | 338.5 us | +1.4% | 21.36 KB | 21.36 KB |

The timing confidence intervals overlap for the small regressions. Construction is therefore neutral against the 5%
investigation gate, and allocation is unchanged.

Three 100-iteration manual query runs were compared by their median query-only time:

| Workload | Old | New | Change | Allocation |
| --- | ---: | ---: | ---: | ---: |
| Content text | 195.1 us | 196.4 us | +0.7% | unchanged |
| Product cards with scoped tag scans | 764.7 us | 660.0 us | -13.7% | unchanged |
| Head and body | 258.9 us | 256.4 us | -1.0% | unchanged |
| Adversarial selectors | 2.3 us | 2.3 us | neutral | unchanged |

The full 47-document retained-memory run reported identical compact retained memory (265.14 KB), while total compact
allocation differed by only 0.03 KB over the entire corpus. The final core remains 16 bytes.

The byte-identical 47-document manual parse corpus had a median per-document new/old ratio of 0.953. Individual pages
varied widely in both directions because this runner is sensitive to pooling, caching, and system noise; it is a broad
regression screen, not evidence for page-specific wins. Four pages regressed by more than 5%, while 23 improved by more
than 5%. The controlled BenchmarkDotNet construction result is the decision gate.

## Correctness coverage

- Packed and frozen-layout tests verify subtree bounds, direct children, scoped tag scans, and interval descendant checks.
- The malformed-HTML property oracle covers 10,000 generated inputs against AngleSharp's object DOM.
- The top-level Arena smoke matrix covers both layouts, templates, foreign content, foster parenting, and formatting
  adoption.

## Rejected alternatives

- **Optional subtree-boundary sidecar:** adds retained storage and capability branches although the boundary replaces an
  existing final-only field at no size cost.
- **Keep final next-sibling links:** preserves a mutation-oriented representation and prevents bounded forward scans;
  direct children remain efficient with subtree-end jumps.
- **Store both links in the hot core:** widens every node or moves data to another allocation without a demonstrated
  workload that repays it.
