# Engineering reviews

These documents capture the cleanup review performed against pull request #56 at
`4e86c68342b1295a7f2c3ecb5ac878514ef97493` on 2026-08-02.

- [Compact lane review](2026-08-02-pr-56-compact.md)
- [Compact refactor follow-up](2026-08-02-compact-refactor-follow-up.md)
- [Open GitHub issues audit](2026-08-02-open-issues-audit.md)
- [Streaming lane review](2026-08-02-pr-56-streaming.md)
- [Streaming refactor follow-up](2026-08-03-streaming-refactor-follow-up.md)
- [Repository structure and cleanup review](2026-08-02-pr-56-repository-cleanup.md)

The desired pre-alpha end state is deliberately strict:

- every library project has one clear use case;
- dead experiments and dependencies are removed rather than abstracted;
- implementation details remain internal until an external consumer needs them;
- opinionated conversion and proxy behavior lives in samples;
- performance diagnostics are opt-in and do not tax normal execution;
- public structural-query terminology states whether it follows HTML tree semantics or lexical tag-stack semantics.

These are review snapshots, not permanent architecture documents. Once a finding is resolved, its stable contract should
be represented by tests and the relevant design documentation. The resolved review snapshot can then be removed.
