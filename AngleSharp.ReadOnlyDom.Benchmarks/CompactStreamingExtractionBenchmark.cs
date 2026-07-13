#if NET10_0
using System.Net;
using System.Text;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.Compact;
using AngleSharp.ReadOnlyDom.Html;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[MemoryDiagnoser]
public class CompactStreamingExtractionBenchmark
{
    private readonly string _html = CreatePage();
    private readonly HtmlParser _readOnlyParser = new(default, ReadOnlyParser.DefaultContext);
    private readonly HtmlParser _compactParser = CompactParser.CreateParser();
    private readonly CompactExtractionPlan _compactPlan = CompactExtractionPlan
        .Start("div")
        .WithId("content")
        .TakeFirst()
        .SelectNormalizedText("text", required: true)
        .Compile();
    private readonly CompactStreamingExtractionPlan _streamingPlan = CompactStreamingExtractor
        .CompileFirstNormalizedText();
    private readonly CompactAggregatePlan _aggregatePlan = CompactAggregate
        .First(CompactAggregateSelector.Tag("div").WithId("content"))
        .Field("text", CompactAggregateProjection.SelfNormalizedText(), required: true)
        .Compile();

    [GlobalSetup]
    public void Validate()
    {
        var expected = ReadOnlyParseAndTraverse();
        if (
            CompactParseAndPlan() != expected
            || QueryDirectedConstruction() != expected
            || EofAggregateConstruction() != expected
            || NaiveTokenClose() != expected
        )
            throw new InvalidOperationException("Streaming extraction benchmark implementations disagree.");
    }

    [Benchmark(Baseline = true)]
    public string ReadOnlyParseAndTraverse()
    {
        using var document = _readOnlyParser.ParseReadOnlyDocument(_html);
        return Normalize(document.QueryOne(static node => node.TagId("div", "content"))?.GetTextContent());
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

    [Benchmark]
    public string NaiveTokenClose()
    {
        var start = _html.IndexOf("<div id=content>", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return string.Empty;
        start = _html.IndexOf('>', start) + 1;
        var end = _html.IndexOf("</div>", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            end = _html.Length;
        var text = new StringBuilder(end - start);
        var inTag = false;
        foreach (var character in _html.AsSpan(start, end - start))
        {
            if (character == '<')
                inTag = true;
            else if (character == '>')
                inTag = false;
            else if (!inTag)
                text.Append(character);
        }
        return Normalize(WebUtility.HtmlDecode(text.ToString()));
    }

    private static string CreatePage()
    {
        var html = new StringBuilder("<html><body>");
        for (var index = 0; index < 400; index++)
            html.Append("<div class=noise data-index=").Append(index).Append(">ignored</div>");
        html.Append("<div id=content>");
        for (var index = 0; index < 120; index++)
            html.Append("<p> row ").Append(index).Append(" &amp; <b>value</b> </p>");
        return html.Append("</div><footer>ignored</footer></body></html>").ToString();
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
}
#endif
