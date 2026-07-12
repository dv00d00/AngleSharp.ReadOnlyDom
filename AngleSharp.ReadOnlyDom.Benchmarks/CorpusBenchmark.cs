using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.Filters;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[Config(typeof(Config))]
[MemoryDiagnoser]
public class CorpusBenchmark
{
    private sealed class Config : ManualConfig
    {
        public Config() => AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance));
    }

    private IReadOnlyList<CorpusDocument> _documents = null!;
    private readonly HtmlParser _standardParser = new();
    private readonly HtmlParser _readOnlyParser = new(default, ReadOnlyParser.DefaultContext);

    [ParamsSource(nameof(Tiers))]
    public string Tier { get; set; } = "small";

    public IEnumerable<string> Tiers()
    {
        var selected = Environment.GetEnvironmentVariable("AS_BENCH_CORPUS_TIER");
        return string.IsNullOrWhiteSpace(selected) ? ["small", "full"] : [selected];
    }

    [GlobalSetup]
    public void Setup() => _documents = BenchmarkCorpus.Load(Tier);

    [Benchmark(Baseline = true)]
    public int Standard()
    {
        var count = 0;
        foreach (var source in _documents)
        {
            using var document = _standardParser.ParseDocument(source.Html);
            count += document.All.Length;
        }

        return count;
    }

    [Benchmark]
    public int ReadOnly()
    {
        var count = 0;
        foreach (var source in _documents)
        {
            using var document = _readOnlyParser.ParseReadOnlyDocument(source.Html);
            count += document.AllDescendants().Count();
        }

        return count;
    }

    [Benchmark]
    public int ReadOnlyBody()
    {
        var count = 0;
        foreach (var source in _documents)
        {
            var filter = new FirstTagAndAllChildren("body");
            using var document = _readOnlyParser.ParseReadOnlyDocument(source.Html, filter.Loop);
            count += document.AllDescendants().Count();
        }

        return count;
    }
}
