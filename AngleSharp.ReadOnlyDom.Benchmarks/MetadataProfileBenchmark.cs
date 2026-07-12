using AngleSharp.Html.Parser;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[MemoryDiagnoser]
public class MetadataProfileBenchmark
{
    private HtmlParser _parser = null!;

    [ParamsAllValues]
    public ReadOnlyMetadataProfile Profile { get; set; }

    [GlobalSetup]
    public void Setup() => _parser = ReadOnlyParser.CreateParser(Profile);

    [Benchmark]
    public void ParsePreset()
    {
        using var document = _parser.ParseReadOnlyDocument(StaticHtml.Github);
    }
}
