#if NET10_0
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.ReadOnlyDom.Compact;
using AngleSharp.ReadOnlyDom.Compact.Parsing;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[BenchmarkCategory("Parsing")]
[MemoryDiagnoser]
public class CompactBuildBenchmark
{
    internal static readonly string StructuralPage = CreateStructuralPage();
    private readonly HtmlParser _standardParser = new();
    private readonly HtmlParser _readOnlyParser = ReadOnlyParser.CreateParser(ReadOnlyMetadataProfile.Minimal);
    private readonly HtmlParser _compactParser = CompactParser.CreateParser();
    private readonly HtmlParser _compactParserNoAttributes = CompactParser.CreateParser(
        attributeFilter: static (ref _, _) => false
    );

    [Benchmark]
    public int ParseStandard()
    {
        using var document = _standardParser.ParseDocument(StructuralPage);
        return document.ChildNodes.Length;
    }

    [Benchmark(Baseline = true)]
    public int ParseReadOnly()
    {
        using var document = _readOnlyParser.ParseReadOnlyDocument(StructuralPage);
        return document.ChildNodes.Length;
    }

    [Benchmark]
    public int ParseCompact()
    {
        using var compact = CompactParser.CreateParser().ParseCompactDocument(StructuralPage);
        return compact.NodeCount;
    }

    [Benchmark]
    public int ParseCompactReused()
    {
        using var compact = _compactParser.ParseCompactDocument(StructuralPage);
        return compact.NodeCount;
    }

    [Benchmark]
    public int ParseCompactReusedNoAttributes()
    {
        using var compact = _compactParserNoAttributes.ParseCompactDocument(StructuralPage);
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

#endif
