# Canonical tag metadata

`dom/src/AngleSharp.ReadOnlyDom/Generated/GeneratedTagMetadata.g.cs` is deterministic checked-in code generated from the
paired AngleSharp source revision selected by the repository build. The generator reflects AngleSharp's internal
`HtmlElementFactory`, creates every public `TagNames` entry, and records its effective `NodeFlags`. This makes the paired
runtime construction behavior the canonical source instead of maintaining a second handwritten table.

`dom/src/AngleSharp.ReadOnlyDom.Compact/GeneratedTagMetadata.g.cs` is the same table emitted into the
`AngleSharp.ReadOnlyDom.Compact` namespace, so the compact parser owns its copy instead of depending on the
`AngleSharp.ReadOnlyDom` project for it. `--namespace` selects the emitted namespace; both files must be regenerated
together.

Regenerate after advancing the paired AngleSharp revision:

```powershell
dotnet run --project dom/tools/AngleSharp.ReadOnlyDom.TagGenerator -c Release -- dom/src/AngleSharp.ReadOnlyDom/Generated/GeneratedTagMetadata.g.cs
dotnet run --project dom/tools/AngleSharp.ReadOnlyDom.TagGenerator -c Release -- --namespace AngleSharp.ReadOnlyDom.Compact dom/src/AngleSharp.ReadOnlyDom.Compact/GeneratedTagMetadata.g.cs
dotnet tool run csharpier format dom/src/AngleSharp.ReadOnlyDom/Generated/GeneratedTagMetadata.g.cs dom/src/AngleSharp.ReadOnlyDom.Compact/GeneratedTagMetadata.g.cs
```

Verify that checked-in output is current:

```powershell
dotnet run --project dom/tools/AngleSharp.ReadOnlyDom.TagGenerator -c Release -- --check dom/src/AngleSharp.ReadOnlyDom/Generated/GeneratedTagMetadata.g.cs
dotnet run --project dom/tools/AngleSharp.ReadOnlyDom.TagGenerator -c Release -- --check --namespace AngleSharp.ReadOnlyDom.Compact dom/src/AngleSharp.ReadOnlyDom.Compact/GeneratedTagMetadata.g.cs
```

The output uses a length switch and orders high-frequency corpus tags first within each length. Custom elements retain an
earlier hyphen fast path. `GeneratedTagMetadataTests` independently compares every generated flag against the runtime
AngleSharp factory, and focused tests preserve the read-only specialized form, template, script, and meta types.

The generator targets .NET 10 because it is a repository tool. Generated library code builds on every supported library
target. CI runs the complete tests on `net10.0`; its solution build also compiles the `net472` test target.

## Performance gate

The .NET 10 partial-parse short run after generation measured 4.813 ms / 706.44 KB for the full path and
3.977 ms / 208.24 KB for the filtered path. The committed pre-change signals were 4.817 ms / 706.51 KB and
3.858 ms / 212.08 KB respectively. Full-path performance is unchanged; filtered time is 3.1% higher within the declared
5% short-run noise gate, while allocation fell 1.8%.
