#if NET10_0
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.CompactPrototype;
using AngleSharp.ReadOnlyDom.Html;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[MemoryDiagnoser]
public class FatDocumentParsingBenchmark
{
    private readonly HtmlParser _angleSharpParser = new();
    private readonly HtmlParser _readOnlyParser = new(default, ReadOnlyParser.DefaultContext);
    private readonly CompactParserSession _frozenParser = new(parserOptions: new HtmlParserOptions());
    private string _html = null!;

    [Params("LargeA", "LargeB", "LargeC")]
    public string Document { get; set; } = null!;

    [GlobalSetup]
    public void Setup()
    {
        var corpus = BenchmarkCorpus.LoadLargestAnonymized(3);
        _html = corpus.Single(document => document.Name == Document).Html;

        using var angleSharp = _angleSharpParser.ParseDocument(_html);
        using var readOnly = _readOnlyParser.ParseReadOnlyDocument(_html);
        using var frozen = _frozenParser.Parse(_html.AsMemory());

        if (frozen.Layout != CompactDocumentLayout.FrozenColumns)
            throw new InvalidOperationException($"{Document} requires packed fallback and is not a frozen-view case.");

        var standardElements = CountElements(angleSharp);
        var readOnlyElements = CountElements(readOnly);
        var frozenElements = CountElements(frozen);

        if (standardElements != readOnlyElements || standardElements != frozenElements)
        {
            throw new InvalidOperationException(
                $"{Document} element counts disagree: AngleSharp={standardElements}, "
                    + $"read-only={readOnlyElements}, frozen={frozenElements}."
            );
        }
    }

    [Benchmark(Baseline = true)]
    public int AngleSharpDefault()
    {
        using var document = _angleSharpParser.ParseDocument(_html);
        return document.ChildNodes.Length;
    }

    [Benchmark]
    public int ReadOnlyDom()
    {
        using var document = _readOnlyParser.ParseReadOnlyDocument(_html);
        return document.ChildNodes.Length;
    }

    [Benchmark]
    public int FrozenArena()
    {
        using var document = _frozenParser.Parse(_html.AsMemory());
        return document.NodeCount;
    }

    private static int CountElements(INode node)
    {
        var count = node is IElement ? 1 : 0;
        var children = node is IHtmlTemplateElement template ? template.Content.ChildNodes : node.ChildNodes;
        foreach (var child in children)
            count += CountElements(child);
        return count;
    }

    private static int CountElements(IReadOnlyNode node)
    {
        var count = node is IReadOnlyElement && node is not IReadOnlyDocument ? 1 : 0;
        var children = node is IReadOnlyTemplateElement template ? template.Content : node.ChildNodes;
        foreach (var child in children)
            count += CountElements(child);
        return count;
    }

    private static int CountElements(CompactDocument document)
    {
        var count = 0;
        for (var handle = 0; handle < document.NodeCount; handle++)
        {
            if (document.GetNode(handle).Kind == CompactNodeKind.Element)
                count++;
        }
        return count;
    }
}
#endif
