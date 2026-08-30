# AngleSharp.ReadOnlyDom samples

Run from the repository root:

```powershell
dotnet run --project dom/samples/AngleSharp.ReadOnlyDom.Samples -c Release
```

Opinionated Compact conversion examples are kept in the focused sibling project
`AngleSharp.ReadOnlyDom.ExtractionExamples`. Streaming samples live under the separate `streaming/` product root.

The retained/construction sections parse the same fixture and extract the same article.

| Section | Retained representation | Result ownership | Best fit |
| --- | --- | --- | --- |
| RODOM | Read-only object graph | Values belong to the document | Navigation, metadata profiles, diagnostics and familiar node APIs |
| COMPACT | Columnar compact document | Attribute slices may borrow from the document; normalized text is owned | Repeated known queries with lower retained cost |
| COMPACT EOF PROJECTION | No escaping DOM; temporary construction topology only | Returned projection fields are owned | One known row shape evaluated at EOF |

Compact EOF projection is not bounded-memory input streaming: it consumes a rooted string, runs the complete AngleSharp
tokenizer and HTML tree builder, and evaluates over the temporary construction arena at EOF. This preserves
malformed-HTML semantics while avoiding a retained DOM.

The sample demonstrates:

- `ReadOnlyParser.CreateParser` with `ReadOnlyMetadataProfile.SourceMapped`;
- query helpers such as `QueryOne`, `TagId`, and `CountTagClass`;
- `CompactParser` with optional parent links and allocation-free compact queries;
- `CompactProjection` for owned rows, JSON output, attributes, and normalized text;
- explicit ownership differences between the object and compact DOM representations.
