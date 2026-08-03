#if NET10_0
using AngleSharp.ReadOnlyDom.Compact.Arena;
using AngleSharp.ReadOnlyDom.Compact.Document;
using AngleSharp.ReadOnlyDom.Compact.Parsing;
using AngleSharp.Text;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks.Suites.Parsing;

/// <summary>
/// Separates compact construction lifecycle costs. ConstructTree includes arena initialization;
/// subtract RentAndInitializeArena to estimate tree-builder work. Publication is measured through
/// invocation setup so parsing is excluded from its result.
/// </summary>
[BenchmarkCategory("Parsing", "Diagnostics")]
[MemoryDiagnoser]
public class CompactBuildStageBenchmark
{
    private const int PublicationBatchSize = 64;
    private readonly IArenaHtmlParser _parser = (IArenaHtmlParser)CompactParser.CreateParser();
    private readonly ArenaDocument?[] _publicationInputs = new ArenaDocument[PublicationBatchSize];
    private readonly CompactDocument?[] _published = new CompactDocument[PublicationBatchSize];

    [Benchmark]
    public int RentAndInitializeArena()
    {
        using var document = _parser.CreateArenaDocument(CreateSource());
        return document.Arena.NodeCount;
    }

    [Benchmark]
    public int ConstructTree()
    {
        using var document = _parser.ParseArenaDocument(CreateSource());
        return document.Arena.NodeCount;
    }

    [IterationSetup(Target = nameof(PublishFrozen))]
    public void PreparePublication()
    {
        for (var i = 0; i < PublicationBatchSize; i++)
            _publicationInputs[i] = _parser.ParseArenaDocument(CreateSource());
    }

    [Benchmark(OperationsPerInvoke = PublicationBatchSize)]
    public int PublishFrozen()
    {
        var nodeCount = 0;
        for (var i = 0; i < PublicationBatchSize; i++)
        {
            var document = _publicationInputs[i]!.CreateCompactDocument();
            _published[i] = document;
            nodeCount += document.NodeCount;
        }
        return nodeCount;
    }

    [IterationCleanup(Target = nameof(PublishFrozen))]
    public void CleanupPublication()
    {
        for (var i = 0; i < PublicationBatchSize; i++)
        {
            _publicationInputs[i]?.Dispose();
            _published[i]?.Dispose();
            _publicationInputs[i] = null;
            _published[i] = null;
        }
    }

    private static TextSource CreateSource() => new(new StringTextSource(CompactBuildBenchmark.StructuralPage));
}
#endif
