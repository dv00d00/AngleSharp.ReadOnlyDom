#if NET10_0
using System.Text;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.Compact;
using AngleSharp.ReadOnlyDom.Html;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

/// <summary>
/// Full-input workload with a small result near EOF. The construction-time paths still run the complete AngleSharp
/// tokenizer and tree builder; their advantage comes only from avoiding irrelevant value and DOM materialization.
/// </summary>
[MemoryDiagnoser]
public class LongSyntheticConstructionBenchmark
{
    private readonly HtmlParser _readOnlyParser = new(default, ReadOnlyParser.DefaultContext);
    private readonly HtmlParser _compactParser = CompactParser.CreateParser();
    private readonly CompactExtractionPlan _compactPlan = CompactExtractionPlan
        .Start("article")
        .WithId("content")
        .TakeFirst()
        .SelectNormalizedText("text", required: true)
        .Compile();
    private readonly CompactStreamingExtractionPlan _streamingPlan =
        CompactStreamingExtractor.CompileFirstNormalizedText("article", "content");
    private readonly CompactAggregatePlan _aggregatePlan = CompactAggregate
        .First(CompactAggregateSelector.Tag("article").WithId("content"))
        .Field("text", CompactAggregateProjection.SelfNormalizedText(), required: true)
        .Compile();
    private string _html = null!;

    [Params(5_000)]
    public int NoiseBlocks { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _html = CreatePage(NoiseBlocks);
        var readOnly = ReadOnlyParseAndTraverse();
        var compact = CompactParseAndPlan();
        var queryDirected = QueryDirectedConstruction();
        var aggregate = EofAggregateConstruction();
        AssertEqual("compact", readOnly, compact);
        AssertEqual("query-directed", readOnly, queryDirected);
        AssertEqual("EOF aggregate", readOnly, aggregate);

        var counters = _streamingPlan.Execute(_html).Counters;
        Console.WriteLine(
            $"Synthetic fixture: {Encoding.UTF8.GetByteCount(_html):N0} UTF-8 bytes, "
                + $"{NoiseBlocks:N0} noise blocks, target near EOF, {counters.TokensProcessed:N0} tokens, "
                + $"{counters.NodesMaterialized:N0} topology nodes, {counters.AttributesRetained:N0} retained attributes, "
                + $"early terminated={counters.EarlyTerminated}."
        );
    }

    [Benchmark(Baseline = true)]
    public string ReadOnlyParseAndTraverse()
    {
        using var document = _readOnlyParser.ParseReadOnlyDocument(_html);
        return Normalize(document.QueryOne(static node => node.TagId("article", "content"))?.GetTextContent());
    }

    [Benchmark]
    public string CompactParseAndPlan()
    {
        using var document = _compactParser.ParseCompactDocument(_html);
        var result = _compactPlan.Execute(document);
        return result.Rows.Count == 0 ? string.Empty : result.Rows[0]["text"].Own();
    }

    [Benchmark]
    public string QueryDirectedConstruction() => _streamingPlan.Execute(_html).Value.Own();

    [Benchmark]
    public string EofAggregateConstruction()
    {
        var result = _aggregatePlan.Execute(_html);
        return result.Rows.Count == 0 ? string.Empty : result.Rows[0]["text"].Own();
    }

    private static string CreatePage(int noiseBlocks)
    {
        var html = new StringBuilder(noiseBlocks * 320);
        html.Append("<!doctype html><html><head><title>Synthetic extraction corpus</title></head><body><main>");
        for (var index = 0; index < noiseBlocks; index++)
        {
            html.Append("<section class=card data-index=")
                .Append(index)
                .Append(" data-key=product-")
                .Append(index)
                .Append(" data-region=unused aria-label='Synthetic card ")
                .Append(index)
                .Append("' title='Ignored title' role=group lang=en dir=ltr>")
                .Append("<h2>Unrelated heading ")
                .Append(index)
                .Append("</h2><p>Long irrelevant description with entity &amp; number ")
                .Append(index)
                .Append(" and <span data-value='discard-me'>nested text</span>.</p>")
                .Append("<a href='/noise/")
                .Append(index)
                .Append("' rel=nofollow data-track='ignored'>irrelevant link</a></section>");
        }

        html.Append("<article id=content data-kind=target><h1>Wanted result</h1>");
        for (var index = 0; index < 32; index++)
            html.Append("<p>Useful row ").Append(index).Append(": <span>value</span>.</p>");
        return html.Append("</article></main></body></html>").ToString();
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var output = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = output.Length != 0;
                continue;
            }
            if (pendingSpace)
            {
                output.Append(' ');
                pendingSpace = false;
            }
            output.Append(character);
        }
        return output.ToString();
    }

    private static void AssertEqual(string implementation, string expected, string actual)
    {
        if (actual == expected)
            return;

        var mismatch = 0;
        var sharedLength = Math.Min(expected.Length, actual.Length);
        while (mismatch < sharedLength && expected[mismatch] == actual[mismatch])
            mismatch++;
        var contextStart = Math.Max(0, mismatch - 30);
        var expectedContext = expected.Substring(contextStart, Math.Min(60, expected.Length - contextStart));
        var actualContext = actual.Substring(contextStart, Math.Min(60, actual.Length - contextStart));
        throw new InvalidOperationException(
            $"Long synthetic {implementation} result differs at character {mismatch}; "
                + $"expected length {expected.Length}, actual length {actual.Length}; "
                + $"expected context '{expectedContext}', actual context '{actualContext}'."
        );
    }
}
#endif
