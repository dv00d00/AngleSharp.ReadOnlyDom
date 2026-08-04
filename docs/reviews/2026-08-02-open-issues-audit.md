# Open GitHub issues audit and disposition

The initial audit reviewed 12 open issues on 2026-08-02. After the Compact and Streaming cleanup landed on `main`, the
recommended issue hygiene was applied on 2026-08-03.

## Closed or moved

| Issue | Disposition |
| --- | --- |
| [#41 Producer-consumer parsing experiment](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/41) | Closed as an unscheduled experiment without a benchmark owner or time box. |
| [#48 Byte and async-stream parse entry points](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/48) | Closed as completed. Byte-memory parsing exists for both retained DOMs, and Compact exposes bounded async stream parsing. |
| [#49 AngleSharp core `document.open()` corruption](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/49) | Moved to [AngleSharp #1276](https://github.com/AngleSharp/AngleSharp/issues/1276) and closed locally. |
| [#50 Construction-time token-filter combinators](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/50) | Closed as not planned. Existing benchmark/test filters should be localized or removed rather than expanded into a public lexical selector surface. |
| [#51 Projection-first streaming extraction](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/51) | Closed as superseded by the policy-free query mechanism plus runnable typed, NDJSON, text, and Markdown examples. |
| [#52 Typed and owned Compact mapping](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/52) | Closed as superseded. It described removed extraction types and borrowed results; Compact projection now returns owned values. |
| [#53 Named extraction parser presets](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/53) | Closed as not planned. Explicit parser options and documented defaults preserve a smaller pre-alpha contract. |

## Active roadmap

| Issue | Current scope |
| --- | --- |
| [#42 Corrected topology for stream queries](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/42) | Highest-priority Streaming correctness boundary. It now describes a separate corrected-event mode without weakening the stable lexical contract. |
| [#43 Full-fidelity columnar DOM fallback](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/43) | Compact fidelity contract, html5lib tree-construction parity, and measured remaining stage costs. Existing handle construction, byte/stream inputs, focused parity tests, and stage benchmark are recorded as completed groundwork. |
| [#44 Arena-owned construction text](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/44) | Narrow measured `TextRef` experiment. The completed wrapper-removal work is no longer presented as outstanding. |
| [#54 Object-DOM selector compatibility](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/54) | Coherent lower-priority object-DOM enhancement, independent of Compact projection selectors. |
| [#9 NuGet publishing](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/9) | Release infrastructure backlog, explicitly blocked on package dependency and contract decisions. |
| [#57 Paired-repository CI and formatter pinning](https://github.com/dv00d00/AngleSharp.ReadOnlyDom/issues/57) | Immediate repository infrastructure: paired AngleSharp checkout, source-reference verification, build/test gates, SDK policy, and a local CSharpier manifest. |
