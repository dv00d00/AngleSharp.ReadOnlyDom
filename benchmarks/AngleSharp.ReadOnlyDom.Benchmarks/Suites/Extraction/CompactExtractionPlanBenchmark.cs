#if NET10_0
using System.Text;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.Compact;
using AngleSharp.ReadOnlyDom.Html;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[BenchmarkCategory("Extraction")]
[MemoryDiagnoser]
public class CompactExtractionPlanBenchmark
{
    private readonly string _html = CreatePage();
    private readonly CompactExtractionPlan _plan = CompactExtractionPlan
        .Start("div")
        .WithId("content")
        .TakeFirst()
        .SelectNormalizedText("text", required: true)
        .Compile();
    private IReadOnlyDocument _readOnly = null!;
    private CompactDocument _compact = null!;
    private CompactBoundExtractionPlan _boundPlan = null!;

    [GlobalSetup]
    public void Setup()
    {
        _readOnly = new HtmlParser(default, ReadOnlyParser.DefaultContext).ParseReadOnlyDocument(_html);
        _compact = CompactParser.CreateParser().ParseCompactDocument(_html);
        _boundPlan = _plan.Bind(_compact);
        var expected = ReadOnlyTraversal();
        if (HandWrittenCompactScan() != expected || InterpretedCompactPlan() != expected)
            throw new InvalidOperationException("Extraction implementations disagree.");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _readOnly.Dispose();
        _compact.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int ReadOnlyTraversal()
    {
        var content = _readOnly.QueryOne(static node => node.TagId("div", "content"));
        return Normalize(content?.GetTextContent()).Length;
    }

    [Benchmark]
    public int HandWrittenCompactScan()
    {
        var content = _compact.Elements("div").WithAttribute("id", "content").First();
        return content.Exists ? Normalize(content.Text()).Length : 0;
    }

    [Benchmark]
    public int InterpretedCompactPlan()
    {
        var result = _boundPlan.Execute();
        return result.Rows.Count == 0 ? 0 : result.Rows[0]["text"].Span.Length;
    }

    private static string CreatePage()
    {
        var html = new StringBuilder("<html><body>");
        for (var index = 0; index < 400; index++)
            html.Append("<div class=noise data-index=").Append(index).Append(">ignored</div>");
        html.Append("<div id=content>");
        for (var index = 0; index < 120; index++)
            html.Append("<p> row ").Append(index).Append(" <b>value</b> </p>");
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
