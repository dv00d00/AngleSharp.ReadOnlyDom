#if NET10_0
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.ReadOnlyDom.CompactPrototype;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[MemoryDiagnoser]
public class CompactBuildBenchmark
{
    private static readonly string StructuralPage = CreateStructuralPage();
    private readonly HtmlParser _readOnlyParser = ReadOnlyParser.CreateParser(ReadOnlyMetadataProfile.Minimal);
    private readonly CompactParserSession _compactParser = new();
    private readonly CompactParserSession _compactParserNoAttributes = new(
        attributeFilter: static (ref StructHtmlToken _, ReadOnlyMemory<char> _) => false
    );

    [Benchmark(Baseline = true)]
    public int ParseReadOnly()
    {
        using var document = _readOnlyParser.ParseReadOnlyDocument(StructuralPage);
        return document.ChildNodes.Length;
    }

    [Benchmark]
    public int ParseCompact()
    {
        using var compact = CompactParser.Parse(StructuralPage);
        return compact.NodeCount;
    }

    [Benchmark]
    public int ParseCompactReused()
    {
        using var compact = _compactParser.Parse(StructuralPage);
        return compact.NodeCount;
    }

    [Benchmark]
    public int ParseCompactReusedNoAttributes()
    {
        using var compact = _compactParserNoAttributes.Parse(StructuralPage);
        return compact.NodeCount;
    }

    private static string CreateStructuralPage() =>
        "<html><body><main>"
        + string.Concat(
            Enumerable
                .Range(0, 160)
                .Select(index =>
                    $"<article id='item-{index}' class='entry'><h2>Item {index}</h2><p>Value {index % 11}</p></article>"
                )
        )
        + "</main></body></html>";
}

[MemoryDiagnoser]
public class CompactSetupBenchmark
{
    [Benchmark(Baseline = true)]
    public HtmlParser CreateReadOnlyParser() => ReadOnlyParser.CreateParser(ReadOnlyMetadataProfile.Minimal);

    [Benchmark]
    public CompactParserSession CreateCompactParser() => new();
}

#endif
