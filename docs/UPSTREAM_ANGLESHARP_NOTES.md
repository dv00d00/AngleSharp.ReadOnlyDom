# AngleSharp upstream dependencies

The object and compact DOM projects exercise generic construction and byte-source APIs that are not in a released
AngleSharp package yet. Until they ship, `Directory.Build.targets` replaces the AngleSharp package reference with a
source checkout. The standalone streaming project does not use that override.

## Upstream tracking

Status snapshot: 2026-08-20. All three pull requests are open and mergeable; their Linux and Windows jobs compile the
changes and then hit the same Fallout/.NET SDK infrastructure failure described in the PR comments.

| Pull request | Relationship to this repository |
| --- | --- |
| [AngleSharp/AngleSharp#1285](https://github.com/AngleSharp/AngleSharp/pull/1285) | Handle-oriented HTML tree construction used by the compact arena |
| [AngleSharp/AngleSharp#1286](https://github.com/AngleSharp/AngleSharp/pull/1286) | Direct `ReadOnlyMemory<byte>` parsing in the retained DOM lanes |
| [AngleSharp/AngleSharp#1287](https://github.com/AngleSharp/AngleSharp/pull/1287) | Related opt-in custom-DOM whitespace contract; not enabled by default here |

Do not publish the retained DOM packages against the temporary source contract. After #1285 and #1286 land in an
AngleSharp release, remove the source override, update the central package version, and run the full multi-target suite
against the package before making either project packable. #1287 can land independently.

## Handle-oriented tree construction

FsCheck differential testing against AngleSharp 1.5.2 reduced a mismatch to malformed table content such as:

```html
<table><div><span attr='unterminated>
```

The standard mutable DOM foster-parents `div` into `body` before `table`. The old generic construction path placed it
outside `html` because foster-parenting checks depended on AngleSharp's concrete element types. The handle-oriented
tree builder uses parser flags and HTML tag identity instead, so compact parsing can handle foster parenting, templates,
and formatting adoption directly through `ArenaHandle`, without a parallel object tree. Differential and smoke coverage
keeps the malformed-table case as a regression contract.

## Standalone UTF-8 lane

The streaming product owns its tokenizer, WHATWG entity table, encoding-label table, limits, and sink contract. It has
no runtime dependency on AngleSharp and can be built, tested, and published independently of the upstream work above.
