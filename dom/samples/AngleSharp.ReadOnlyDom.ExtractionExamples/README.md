# Extraction examples

This console sample keeps two opinionated Compact DOM conversions outside the libraries:

- normalized text over a corrected Compact DOM;
- the former Compact Markdown idea, adapted into a sample-local projection over public Compact node cursors.

```powershell
dotnet run --project dom/samples/AngleSharp.ReadOnlyDom.ExtractionExamples -c Release

# Or run both conversions over a file whose target article has id="content":
dotnet run --project dom/samples/AngleSharp.ReadOnlyDom.ExtractionExamples -c Release -- page.html
```

The examples use AngleSharp's corrected HTML topology. The Markdown writer is deliberately incomplete presentation policy,
not a proposed `CompactFieldProjection` kind. The EOF projection arena remains internal, so a custom structural writer
uses a retained Compact document and disposes it immediately after producing its owned string.
