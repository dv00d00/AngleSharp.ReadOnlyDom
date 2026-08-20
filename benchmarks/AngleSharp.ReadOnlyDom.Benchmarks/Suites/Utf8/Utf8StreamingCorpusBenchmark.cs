#if NET10_0
using System.Text;
using AngleSharp.ReadOnlyDom.Benchmarks.Support;
using AngleSharp.ReadOnlyDom.Streaming.Query;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks.Suites.Utf8;

/// <summary>
/// Sweeps the streaming query across the whole corpus rather than the six documents the product
/// comparison uses, so per-document variance is visible with BenchmarkDotNet's statistics instead of
/// hand-rolled interleaving. The plan mirrors the native console's match/generic workload -
/// <c>a[href]</c> counted through OnStart - so numbers here are comparable with the cross-engine runs.
///
/// The GC mode is whatever the host process was launched with; run under both and report which.
/// Set AS_BENCH_CORPUS_TIER to "small" for a quick pass, "full" (default) for everything.
/// </summary>
[BenchmarkCategory("Utf8")]
[MemoryDiagnoser]
public class Utf8StreamingCorpusBenchmark
{
    private static readonly QueryPlan<CountState> Plan = CreatePlan();

    private byte[] _utf8 = null!;

    [ParamsSource(nameof(Documents))]
    public string Document { get; set; } = string.Empty;

    public static IEnumerable<string> Documents()
    {
        var tier = Environment.GetEnvironmentVariable("AS_BENCH_CORPUS_TIER");
        return BenchmarkCorpus
            .Load(string.IsNullOrWhiteSpace(tier) ? "full" : tier)
            .Select(static document => document.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
    }

    [GlobalSetup]
    public void Setup()
    {
        var tier = Environment.GetEnvironmentVariable("AS_BENCH_CORPUS_TIER");
        var source = BenchmarkCorpus
            .Load(string.IsNullOrWhiteSpace(tier) ? "full" : tier)
            .Single(document => document.Name == Document);
        _utf8 = Encoding.UTF8.GetBytes(source.Html);

        // Fail loudly rather than silently benchmarking a query that matches nothing.
        if (Plan.Execute(_utf8, new CountState(), Utf8InputContract.WellFormedUtf8).Count == 0)
            Console.WriteLine($"warning: {Document} produced no matches for a[href]");
    }

    /// <summary>Whole-buffer execution over trusted UTF-8: the tokenizer and query, nothing else.</summary>
    [Benchmark(Baseline = true)]
    public int Contiguous() => Plan.Execute(_utf8, new CountState(), Utf8InputContract.WellFormedUtf8).Count;

    /// <summary>Chunked push, the shape a socket delivers. 4 KiB matches the cross-engine harness.</summary>
    [Benchmark]
    public int Pushed4K()
    {
        using var session = Plan.CreateSession(new CountState(), Utf8InputContract.WellFormedUtf8);
        for (var offset = 0; offset < _utf8.Length; offset += 4096)
            session.Write(_utf8.AsSpan(offset, Math.Min(4096, _utf8.Length - offset)));
        return session.Complete().Count;
    }

    private static QueryPlan<CountState> CreatePlan()
    {
        var anchor = StreamQuery.For<CountState>("a").Attribute("href");
        anchor.OnStart(static (ref CountState state, in Element _) => state.Count++);
        return anchor.Compile();
    }

    public sealed class CountState
    {
        public int Count;
    }
}
#endif
