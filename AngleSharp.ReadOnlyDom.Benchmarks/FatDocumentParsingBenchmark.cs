#if NET10_0
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.Compact;
using AngleSharp.ReadOnlyDom.Html;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[MemoryDiagnoser]
[GcServer(true)]
public class FatDocumentParsingBenchmark
{
    private static readonly HtmlParserOptions NonTrackingOptions = new()
    {
        SkipComments = true,
        SkipProcessingInstructions = true,
        IsKeepingSourceReferences = false,
    };

    private static readonly HtmlParserOptions SourceTrackingOptions = new()
    {
        SkipComments = true,
        SkipProcessingInstructions = true,
        IsKeepingSourceReferences = true,
    };

    private readonly HtmlParser _readOnlyParser = new(
        NonTrackingOptions,
        ReadOnlyParser.CreateContext(ReadOnlyMetadataProfile.Minimal)
    );
    private readonly HtmlParser _readOnlySourceParser = new(
        SourceTrackingOptions,
        ReadOnlyParser.CreateContext(ReadOnlyMetadataProfile.SourceMapped)
    );
    private readonly CompactParserSession _frozenParser = new(parserOptions: NonTrackingOptions);
    private readonly CompactParserSession _frozenSourceParser = new(
        options: CompactMetadataOptions.SourceLocations,
        parserOptions: SourceTrackingOptions
    );
    private string _html = null!;

    [Params("LargeA", "LargeB", "LargeC")]
    public string Document { get; set; } = null!;

    [GlobalSetup]
    public void Setup()
    {
        var corpus = BenchmarkCorpus.LoadLargestAnonymized(3);
        _html = corpus.Single(document => document.Name == Document).Html;

        using var readOnly = _readOnlyParser.ParseReadOnlyDocument(_html);
        using var readOnlySource = _readOnlySourceParser.ParseReadOnlyDocument(_html);
        using var frozen = _frozenParser.Parse(_html);
        using var frozenSource = _frozenSourceParser.Parse(_html);

        if (frozen.Layout != CompactDocumentLayout.FrozenColumns || frozenSource.Layout != CompactDocumentLayout.FrozenColumns)
            throw new InvalidOperationException($"{Document} requires packed fallback and is not a frozen-view case.");

        if (!frozenSource.HasSourceLocations)
            throw new InvalidOperationException("Source-tracking arena did not retain source locations.");

        var readOnlyElements = CountElements(readOnly);
        var readOnlySourceElements = CountElements(readOnlySource);
        var frozenElements = CountElements(frozen);
        var frozenSourceElements = CountElements(frozenSource);

        if (
            readOnlyElements != readOnlySourceElements
            || readOnlyElements != frozenElements
            || readOnlyElements != frozenSourceElements
        )
        {
            throw new InvalidOperationException(
                $"{Document} element counts disagree: read-only={readOnlyElements}, "
                    + $"read-only-source={readOnlySourceElements}, frozen={frozenElements}, "
                    + $"frozen-source={frozenSourceElements}."
            );
        }
    }

    [Benchmark(Baseline = true)]
    public int ReadOnlyDom()
    {
        using var document = _readOnlyParser.ParseReadOnlyDocument(_html);
        return document.ChildNodes.Length;
    }

    [Benchmark]
    public int FrozenArena()
    {
        using var document = _frozenParser.Parse(_html);
        return document.NodeCount;
    }

    [Benchmark]
    public int ReadOnlyDomSourceMapped()
    {
        using var document = _readOnlySourceParser.ParseReadOnlyDocument(_html);
        return document.ChildNodes.Length;
    }

    [Benchmark]
    public int FrozenArenaSourceLocations()
    {
        using var document = _frozenSourceParser.Parse(_html);
        return document.NodeCount;
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
