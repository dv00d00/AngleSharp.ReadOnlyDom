#if NET10_0
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.CompactPrototype;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[MemoryDiagnoser]
public class DirectCompactBuildBenchmark
{
    private readonly HtmlParser _readOnlyParser = ReadOnlyParser.CreateParser(ReadOnlyMetadataProfile.Minimal);

    [Benchmark(Baseline = true)]
    public int ParseReadOnly()
    {
        using var document = _readOnlyParser.ParseReadOnlyDocument(StaticHtml.Github);
        return document.ChildNodes.Length;
    }

    [Benchmark]
    public int ParseReadOnlyThenCompact()
    {
        using var document = _readOnlyParser.ParseReadOnlyDocument(StaticHtml.Github);
        using var compact = CompactDomBuilder.Build(document);
        return compact.NodeCount;
    }

    [Benchmark]
    public int ParseDirectHotCompact()
    {
        using var compact = DirectCompactParser.Parse(StaticHtml.Github);
        return compact.NodeCount;
    }

    [Benchmark]
    public int ParseDirectHotCompactPooled()
    {
        using var compact = DirectCompactParser.Parse(StaticHtml.Github, ownership: CompactBufferOwnership.Pooled);
        return compact.NodeCount;
    }
}

[MemoryDiagnoser]
public class HotCompactLocalityBenchmark
{
    private CompactDocument _wide = null!;
    private HotCompactDocument _hot = null!;
    private ushort _hotDiv;

    [GlobalSetup]
    public void Setup()
    {
        using var source = ReadOnlyParser
            .CreateParser(ReadOnlyMetadataProfile.Minimal)
            .ParseReadOnlyDocument(StaticHtml.Github);
        _wide = CompactDomBuilder.Build(source);
        _hot = DirectCompactParser.Parse(StaticHtml.Github, CompactMetadataOptions.ParentLinks);
        _hotDiv = _hot.FindNameId("div");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _wide.Dispose();
        _hot.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int ScanWideNodes()
    {
        var checksum = 0;
        for (var i = 0; i < _wide.NodeCount; i++)
            checksum += (int)_wide.GetNode(i).Kind;
        return checksum;
    }

    [Benchmark]
    public int ScanHotNodes()
    {
        var checksum = 0;
        for (var i = 0; i < _hot.NodeCount; i++)
            checksum += (int)_hot.GetNode(i).Kind;
        return checksum;
    }

    [Benchmark]
    public int FindDivsWide() => _wide.FindElements("div").Count();

    [Benchmark]
    public int FindDivsHot() => _hot.CountElements(_hotDiv);

    [Benchmark]
    public int TraverseHotLinked()
    {
        var count = 0;
        var handle = 0;
        while (handle >= 0)
        {
            count++;
            ref readonly var node = ref _hot.GetNode(handle);
            handle = node.FirstChild >= 0 ? node.FirstChild : NextAfterSubtree(handle);
        }
        return count;
    }

    private int NextAfterSubtree(int handle)
    {
        while (handle >= 0)
        {
            var sibling = _hot.GetNode(handle).NextSibling;
            if (sibling >= 0)
                return sibling;
            if (!_hot.HasParentLinks)
                return -1;
            handle = _hot.GetParent(handle);
        }
        return -1;
    }
}
#endif
