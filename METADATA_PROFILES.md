# Metadata profiles

`ReadOnlyParser.CreateParser(profile)` is the supported opt-in entry point. Profiles are immutable and parser-scoped;
`ReadOnlyParser.DefaultContext` retains the historical minimal construction behavior.

| Profile | Parent navigation | Qualified names | Source map | Diagnostics | Comments | Processing instructions |
| --- | --- | --- | --- | --- | --- | --- |
| `Minimal` | Yes | Derived | No | No | No | No |
| `Navigable` | Yes | Derived | No | No | Yes | No |
| `SourceMapped` | Yes | Derived | Positions | No | No | No |
| `Diagnostic` | Yes | Derived | Positions | Yes | Yes | Yes |

Parent links are part of every current profile because AngleSharp construction and the public traversal contract require
them. Namespace and node kind are derived from `NodeFlags`; prefix and local name are derived from the qualified node name.
They add no metadata field or store entry.

`Minimal` creates neither a document metadata container nor per-node metadata. `SourceMapped` and `Diagnostic` allocate a
document-owned source dictionary and a weak routing entry for each element so AngleSharp's current element-level
`SourceReference` setter can assign into that dictionary. `Diagnostic` additionally allocates a document-owned error list.
Capabilities are queried with `TryGetSourceMetadata` and `TryGetDiagnostics`; unavailable capabilities are not represented
by misleading empty collections.

Source fidelity names form an explicit public contract: `Offsets`, `Positions`, and `Tokens`. The current AngleSharp bridge
provides position-bearing source references for both source-capable presets; token retention is defined for a future bridge. A future
AngleSharp construction-factory hook such as `SetSourceReference(document, element, reference)` would let this library
remove the weak assignment-routing bridge while keeping metadata document-owned.

The legacy combination of `ReadOnlyParser.DefaultContext` with
`HtmlParserOptions.IsKeepingSourceReferences = true` remains compatible and uses sparse weak element storage. New code
should use `SourceMapped` or `Diagnostic` so ownership and capability costs are explicit.

## .NET 10 measurements

The checked-in benchmark matrix measures every profile. A short-run GitHub-page sample on .NET 10.0.9 produced:

| Profile | Mean | Allocated |
| --- | ---: | ---: |
| Minimal | 4.990 ms | 860.80 KB |
| Navigable | 4.854 ms | 866.52 KB |
| SourceMapped | 10.407 ms | 2,232.19 KB |
| Diagnostic | 10.510 ms | 2,300.59 KB |

A three-repetition small-corpus retained-memory run retained 62.16 MB for Minimal, 62.17 MB for Navigable, 147.62 MB for
SourceMapped, and 147.62 MB for Diagnostic. These are local short-run signals rather than release baselines; use
`./scripts/bench.ps1 all` on the exact release commit for stable comparison.
