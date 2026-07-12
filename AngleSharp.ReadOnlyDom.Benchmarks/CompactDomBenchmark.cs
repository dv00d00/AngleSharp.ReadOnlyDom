#if NET10_0
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.CompactPrototype;
using AngleSharp.ReadOnlyDom.Html;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[MemoryDiagnoser]
public class CompactDomBuildBenchmark
{
    private readonly HtmlParser _parser = ReadOnlyParser.CreateParser(ReadOnlyMetadataProfile.Minimal);
    private IReadOnlyDocument _document = null!;

    [GlobalSetup]
    public void Setup() => _document = _parser.ParseReadOnlyDocument(StaticHtml.Github);

    [GlobalCleanup]
    public void Cleanup() => _document.Dispose();

    [Benchmark(Baseline = true)]
    public int ParseReadOnly()
    {
        using var document = _parser.ParseReadOnlyDocument(StaticHtml.Github);
        return Count(document);
    }

    [Benchmark]
    public int BuildCompactFromParsed()
    {
        using var compact = CompactDomBuilder.Build(_document);
        return compact.NodeCount;
    }

    [Benchmark]
    public int ParseThenCompact()
    {
        using var document = _parser.ParseReadOnlyDocument(StaticHtml.Github);
        using var compact = CompactDomBuilder.Build(document);
        return compact.NodeCount;
    }

    private static int Count(IReadOnlyNode node)
    {
        var count = 1;
        foreach (var child in node.ChildNodes)
            count += Count(child);
        return count;
    }
}

[MemoryDiagnoser]
public class CompactDomTraversalBenchmark
{
    private IReadOnlyDocument _readOnly = null!;
    private CompactDocument _compact = null!;

    [GlobalSetup]
    public void Setup()
    {
        _readOnly = ReadOnlyParser
            .CreateParser(ReadOnlyMetadataProfile.Minimal)
            .ParseReadOnlyDocument(StaticHtml.Github);
        _compact = CompactDomBuilder.Build(_readOnly);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _compact.Dispose();
        _readOnly.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int TraverseReadOnly() => Count(_readOnly);

    [Benchmark]
    public int TraverseCompactLinked()
    {
        var count = 0;
        var pending = new Stack<int>();
        pending.Push(0);
        while (pending.TryPop(out var handle))
        {
            count++;
            foreach (var child in _compact.Children(handle))
                pending.Push(child);
        }

        return count;
    }

    [Benchmark]
    public int TraverseCompactLinear()
    {
        var checksum = 0;
        for (var handle = 0; handle < _compact.NodeCount; handle++)
            checksum += (int)_compact.GetNode(handle).Kind;
        return checksum;
    }

    [Benchmark]
    public int FindDivsReadOnly() => _readOnly.QueryAll(node => node.Tag("div")).Count();

    [Benchmark]
    public int FindDivsCompact() => _compact.FindElements("div").Count();

    [Benchmark]
    public CompactNodeWrapper MaterializeWrappers() => _compact.MaterializeWrapperTree();

    private static int Count(IReadOnlyNode node)
    {
        var count = 1;
        foreach (var child in node.ChildNodes)
            count += Count(child);
        return count;
    }
}

[MemoryDiagnoser]
public class CompactSourceIndexBenchmark
{
    private IReadOnlyDocument _source = null!;
    private CompactDocument _dense = null!;
    private CompactDocument _sparse = null!;
    private CompactDocument _dictionary = null!;

    [GlobalSetup]
    public void Setup()
    {
        _source = ReadOnlyParser
            .CreateParser(ReadOnlyMetadataProfile.SourceMapped)
            .ParseReadOnlyDocument(StaticHtml.Github);
        _dense = Build(CompactIndexMode.Dense);
        _sparse = Build(CompactIndexMode.Sparse);
        _dictionary = Build(CompactIndexMode.Dictionary);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _dense.Dispose();
        _sparse.Dispose();
        _dictionary.Dispose();
        _source.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int LookupDense() => Lookup(_dense);

    [Benchmark]
    public int LookupSparse() => Lookup(_sparse);

    [Benchmark]
    public int LookupDictionary() => Lookup(_dictionary);

    [Benchmark]
    public CompactDocument BuildDense() => Build(CompactIndexMode.Dense);

    [Benchmark]
    public CompactDocument BuildSparse() => Build(CompactIndexMode.Sparse);

    [Benchmark]
    public CompactDocument BuildDictionary() => Build(CompactIndexMode.Dictionary);

    private CompactDocument Build(CompactIndexMode mode) =>
        CompactDomBuilder.Build(_source, new CompactDomOptions { SourceLocationIndexMode = mode });

    private static int Lookup(CompactDocument document)
    {
        var checksum = 0;
        for (var handle = 0; handle < document.NodeCount; handle++)
        {
            if (document.TryGetSourceLocation(handle, out var source))
                checksum += source.Index;
        }
        return checksum;
    }
}
#endif
