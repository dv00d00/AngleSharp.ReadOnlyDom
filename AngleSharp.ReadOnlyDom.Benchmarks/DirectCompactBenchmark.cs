#if NET10_0
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.ReadOnlyDom.CompactPrototype;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[MemoryDiagnoser]
public class DirectCompactBuildBenchmark
{
    private static readonly string StructuralPage = CreateStructuralPage();
    private readonly HtmlParser _readOnlyParser = ReadOnlyParser.CreateParser(ReadOnlyMetadataProfile.Minimal);
    private readonly DirectCompactParserSession _directPooled = new(ownership: CompactBufferOwnership.Pooled);
    private readonly DirectCompactParserSession _directPooledNoAttributes = new(
        ownership: CompactBufferOwnership.Pooled,
        attributeFilter: static (ref StructHtmlToken _, ReadOnlyMemory<char> _) => false
    );

    [Benchmark(Baseline = true)]
    public int ParseReadOnly()
    {
        using var document = _readOnlyParser.ParseReadOnlyDocument(StructuralPage);
        return document.ChildNodes.Length;
    }

    [Benchmark]
    public int ParseDirectHotCompactPooled()
    {
        using var compact = DirectCompactParser.Parse(StructuralPage, ownership: CompactBufferOwnership.Pooled);
        return compact.NodeCount;
    }

    [Benchmark]
    public int ParseDirectHotCompactPooledReused()
    {
        using var compact = _directPooled.Parse(StructuralPage);
        return compact.NodeCount;
    }

    [Benchmark]
    public int ParseDirectHotCompactPooledNoAttributes()
    {
        using var compact = _directPooledNoAttributes.Parse(StructuralPage);
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
public class DirectCompactSetupBenchmark
{
    [Benchmark(Baseline = true)]
    public HtmlParser CreateReadOnlyParser() => ReadOnlyParser.CreateParser(ReadOnlyMetadataProfile.Minimal);

    [Benchmark]
    public DirectCompactParserSession CreateDirectPooled() => new(ownership: CompactBufferOwnership.Pooled);
}

#endif
