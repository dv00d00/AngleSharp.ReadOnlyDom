# AngleSharp.ReadOnlyDom samples

Run from the repository root:

```powershell
dotnet run --project dom/samples/AngleSharp.ReadOnlyDom.Samples -c Release
```

Opinionated conversion and UI examples are kept in focused sibling projects:

- `AngleSharp.ReadOnlyDom.ExtractionExamples`: streaming text, Compact text, and Compact Markdown projection;
- `AngleSharp.ReadOnlyDom.MarkdownProxy`: bounded streaming HTML-to-Markdown endpoint;
- `AngleSharp.ReadOnlyDom.MarkdownNavigation`: every local HTML page is streamed to Markdown before link navigation.

The retained/construction sections parse the same fixture and extract the same article. The UTF-8 stream-query section
uses a small catalogue fixture to demonstrate several result shapes.

| Section | Retained representation | Result ownership | Best fit |
| --- | --- | --- | --- |
| RODOM | Read-only object graph | Values belong to the document | Navigation, metadata profiles, diagnostics and familiar node APIs |
| COMPACT | Columnar compact document | Attribute slices may borrow from the document; normalized text is owned | Repeated known queries with lower retained cost |
| COMPACT EOF PROJECTION | No escaping DOM; temporary construction topology only | Returned projection fields are owned | One known row shape evaluated at EOF |
| UTF-8 STREAM QUERY | No DOM; bounded lexical stack plus query captures | Callback spans borrow UTF-8; strings are decoded only when explicitly requested | Typed rows, subtree text, and arbitrary aggregates directly from UTF-8 |
| STREAMING JSON | No DOM; page and resource-card fields become NDJSON records | Each completed record is published through a backpressured `PipeWriter` | Direct UTF-8 HTML-to-JSON feeds with memory bounded to the current record |

Streaming `Child` and `Descendant` are lexical tag-stack relations. They intentionally do not apply implied end tags,
foster parenting, the adoption agency algorithm, or browser handling of self-closing syntax. Use a retained DOM lane
when corrected HTML-tree relationships are part of the result contract.

Compact EOF projection is not bounded-memory input streaming: it consumes a rooted string, runs the complete AngleSharp
tokenizer and HTML tree builder, and evaluates over the temporary construction arena at EOF. This preserves
malformed-HTML semantics while avoiding a retained DOM.

The sample demonstrates:

- `ReadOnlyParser.CreateParser` with `ReadOnlyMetadataProfile.SourceMapped`;
- query helpers such as `QueryOne`, `TagId`, and `CountTagClass`;
- `CompactParser` with optional parent links and allocation-free compact queries;
- `CompactProjection` for owned rows, JSON output, attributes, and normalized text;
- `StreamQuery` for fluent tag/attribute paths and one completed-element callback instead of manual start/text/end plumbing;
- automatic projection of predicate attributes and explicit projection of optional attributes;
- borrowed `TextUtf8` / `TryGetAttributeUtf8` values and explicit `GetText` / `GetAttribute` ownership;
- typed product rows, normalized subtree text, and a custom page summary using caller-owned state;
- a `PipeReader` -> `StreamQuery` -> `Utf8JsonWriter` -> `PipeWriter` pipeline that turns an HTML resource page into an NDJSON feed;
- derived JSON fields and per-record publication with borrowed callback spans, no retained DOM, and no intermediate content-card list.
