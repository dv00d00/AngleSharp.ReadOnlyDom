# AngleSharp.ReadOnlyDom samples

Run from the repository root:

```powershell
dotnet run --project samples/AngleSharp.ReadOnlyDom.Samples -c Release
```

All sections parse the same HTML fixture and extract the same article. They differ in what remains alive after parsing:

| Section | Retained representation | Result ownership | Best fit |
| --- | --- | --- | --- |
| RODOM | Read-only object graph | Values belong to the document | Navigation, metadata profiles, diagnostics and familiar node APIs |
| COMPACT | Columnar compact document | Attribute slices may borrow from the document; normalized text is owned | Repeated known queries with lower retained cost |
| COMPACT STREAMING | No escaping DOM; temporary construction topology only | Returned text/aggregate fields are owned | One compiled extraction shape per input |

“COMPACT STREAMING” is the existing API family name. The current implementation is construction-time projection, not
bounded-memory input streaming: it consumes a rooted string, runs the complete AngleSharp tokenizer and HTML tree builder,
and finalizes at EOF. This preserves malformed-HTML semantics while avoiding a retained DOM.

The sample demonstrates:

- `ReadOnlyParser.CreateParser` with `ReadOnlyMetadataProfile.SourceMapped`;
- query helpers such as `QueryOne`, `TagId`, and `CountTagClass`;
- `CompactParser` with optional parent links and allocation-free compact queries;
- `CompactExtractionPlan` with borrowed attribute and owned normalized-text fields;
- `CompactStreamingExtractor` for the specialized `first tag#id -> normalized text` view;
- `CompactAggregate` for owned JSON, normalized text, and minimal structural Markdown.
