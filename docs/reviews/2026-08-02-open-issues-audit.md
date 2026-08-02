# Open GitHub issues audit

Reviewed the 12 open issues in `dv00d00/AngleSharp.ReadOnlyDom` on 2026-08-02 against the current Compact projection
cleanup. No issue state or text was changed during this audit.

## Actionable now

| Issue | Assessment | Recommended action |
| --- | --- | --- |
| [#48 Restore byte and async-stream parse entry points](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/48) | Completed. Public byte-memory parsing exists for both object read-only and Compact parsers; public bounded async stream parsing exists for Compact and is covered by tests/benchmark callers. | Close as completed after the current branch lands. |
| [#52 Typed and owned result mapping](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/52) | Mostly stale. The issue names the removed `CompactExtractionPlan`, borrowed ownership, and execution over a retained document. `CompactProjectionPlan` now returns owned values after disposing its temporary arena. Typed mapping remains unproven and would enlarge the pre-alpha contract. | Close as superseded. If a real consumer appears, reopen a smaller ordinal-binding issue; keep the first typed mapper in a sample. |
| [#53 Named extraction parser presets](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/53) | Conflicts with the cleanup direction. The sole opinionated `CompactParserProfiles.Extraction` policy had no external consumer and was removed. Projection already compiles exact internal attribute/text requirements; general parser presets would add policy and `Explain` surface to both libraries. | Close as not planned. Keep full parser options explicit and optimized policies in benchmarks/samples until shared demand exists. |
| [#49 AngleSharp core `document.open()` corruption](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/49) | Valid report, wrong tracker by its own scope note. It is not caused by or fixable in this repository. | File or link the upstream AngleSharp issue, then close this parked copy. |

## Keep open, but update scope

| Issue | Assessment | Recommended action |
| --- | --- | --- |
| [#43 Full-fidelity columnar DOM fallback](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/43) | Still the correct Compact DOM epic. Current construction, topology, byte/stream paths, and differential tests cover substantial ground, but a documented full-fidelity contract, corpus gate, and stage benchmark are not complete. | Keep. Remove the requirement for a public named profile; explicit parser options plus a documented default contract are sufficient. |
| [#44 Native columnar tree builder](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/44) | Not complete. Compact has a handle-backed arena and no retained wrapper tree, but it still consumes AngleSharp's generic construction seam and keeps `StringOrMemory` in construction payloads. | Keep only as the measured implementation child of #43. Do not expose its storage machinery as API. |
| [#51 Projection-first streaming extraction](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/51) | The current lexical query engine already provides bounded callback capture and caller-owned state, while the removed convenience wrappers showed that another production terminal would enlarge the pre-alpha contract. Its proposed production JSON terminal conflicts with the library/sample boundary. | Reassess after #42. Keep JSON and typed outcome resolution in samples; close as superseded unless corrected topology reveals a concrete missing primitive. |
| [#50 Construction-time token-filter combinators](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/50) | The correctness problem is real, but a new public lexical mini-selector surface would expand API while #42 remains unresolved. Compact tokenizer middleware has now been internalized. | Mark blocked by #42 or move the reusable filter to benchmark/sample scope first; do not make it shared public API yet. |
| [#54 CSS selectors over object read-only DOM](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/54) | Independent of Compact and still coherent. It has explicit unsupported-selector behavior and parity gates. | Keep as an object-DOM enhancement; do not couple it to Compact projection selectors. |

## Valid backlog / experiments

| Issue | Assessment | Recommended action |
| --- | --- | --- |
| [#42 Corrected topology for stream queries](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/42) | Still the main streaming correctness boundary and prerequisite for DOM-like structural claims. The current engine now explicitly documents lexical semantics rather than approximating corrected topology. | Keep high priority; do not add isolated optional-end-tag heuristics. |
| [#41 Producer-consumer parsing experiment](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/41) | Explicitly speculative and unrelated to the current pre-alpha surface cleanup. | Close unless a benchmark owner and time box are assigned; the issue can be recreated from its preserved hypothesis. |
| [#9 NuGet publishing](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/9) | Still open; Compact remains pre-alpha and non-packable. | Keep as release infrastructure backlog, blocked on the public-contract decisions above. |

## Net cleanup

Recommended immediate issue hygiene after the branch lands:

- close completed #48;
- close stale/scope-expanding #52 and #53;
- move #49 upstream and close the local copy;
- close #41 unless the experiment is actively scheduled;
- update #43, #50, and #51 to reflect the smaller library/sample boundary.

That reduces the active set from 12 to 7 issues without losing a currently supported use case or correctness obligation.
