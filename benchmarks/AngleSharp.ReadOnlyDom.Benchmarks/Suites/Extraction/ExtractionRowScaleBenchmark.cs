#if NET10_0
using System.Text;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.Compact.Parsing;
using AngleSharp.ReadOnlyDom.Compact.Projection;
using AngleSharp.ReadOnlyDom.Compact.Query;
using AngleSharp.ReadOnlyDom.Html;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks.Suites.Extraction;

/// <summary>
/// Many-rows extraction: every lane produces one owned row per section, so cost scales with
/// rows x fields rather than with finding a single result. This is the counterpart to
/// <see cref="LongSyntheticConstructionBenchmark"/>, which measures a single result near EOF and
/// therefore cannot show per-row projection cost. Sections is the scaling axis.
/// </summary>
[BenchmarkCategory("Extraction", "ManyRows")]
[MemoryDiagnoser]
public class ExtractionRowScaleBenchmark
{
    private readonly HtmlParser _readOnlyParser = new(default, ReadOnlyParser.DefaultContext);
    private readonly HtmlParser _compactParser = CompactParser.CreateParser();
    private readonly CompactProjectionPlan _projectionPlan = CompactProjection
        .ForEach(CompactProjectionSelector.Tag("main").Descendant("section").WithClass("card"))
        .Field("key", CompactFieldProjection.SelfAttribute("data-key"), required: true)
        .Field(
            "heading",
            CompactFieldProjection.FirstNormalizedText(CompactProjectionSelector.Tag("h2")),
            required: true
        )
        .Field("link", CompactFieldProjection.FirstAttribute(CompactProjectionSelector.Tag("a"), "href"))
        .Compile();

    private string _html = null!;

    [Params(1_000, 5_000)]
    public int Sections { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _html = CreatePage(Sections);
        var readOnly = ReadOnlyDomRows();
        var compactScan = CompactScanRows();
        var projection = EofProjectionRows();

        AssertEqual("compact scan", readOnly, compactScan);
        AssertEqual("EOF projection", readOnly, projection);
        Console.WriteLine(
            $"Row-scale fixture: {Encoding.UTF8.GetByteCount(_html):N0} UTF-8 bytes, "
                + $"{Sections:N0} sections -> {readOnly.Count:N0} rows x 3 fields "
                + $"= {readOnly.Count * 3:N0} projected values."
        );
    }

    [Benchmark(Baseline = true)]
    public List<SectionRow> ReadOnlyDomRows()
    {
        using var document = _readOnlyParser.ParseReadOnlyDocument(_html);
        var rows = new List<SectionRow>(Sections);
        foreach (var section in document.QueryAll(static node => node.Tag("section") && node.Attr("class", "card")))
        {
            var heading = section.QueryOne(static node => node.Tag("h2"));
            var link = section.QueryOne(static node => node.Tag("a"));
            rows.Add(
                new SectionRow(
                    Attribute(section, "data-key"),
                    heading?.GetTextContent() ?? string.Empty,
                    link is null ? string.Empty : Attribute(link, "href")
                )
            );
        }
        return rows;
    }

    [Benchmark]
    public List<SectionRow> CompactScanRows()
    {
        using var document = _compactParser.ParseCompactDocument(_html);
        var rows = new List<SectionRow>(Sections);
        foreach (var section in document.Elements("section").WithClass("card"))
        {
            var heading = section.Elements("h2").First();
            var link = section.Elements("a").First();
            rows.Add(
                new SectionRow(
                    section.Attr("data-key").ToString(),
                    heading.Exists ? heading.Text() : string.Empty,
                    link.Exists ? link.Attr("href").ToString() : string.Empty
                )
            );
        }
        return rows;
    }

    [Benchmark]
    public List<SectionRow> EofProjectionRows()
    {
        var result = _projectionPlan.Execute(_html);
        var rows = new List<SectionRow>(result.Rows.Count);
        foreach (var row in result.Rows)
            rows.Add(new SectionRow(row["key"].ToString(), row["heading"].ToString(), row["link"].ToString()));
        return rows;
    }

    private static string Attribute(IReadOnlyNode node, string name) =>
        node is IReadOnlyElement element ? element.Attributes[name]?.Value.ToString() ?? string.Empty : string.Empty;

    private static string CreatePage(int sections)
    {
        var html = new StringBuilder(sections * 320);
        html.Append("<!doctype html><html><head><title>Row scale corpus</title></head><body><main>");
        for (var index = 0; index < sections; index++)
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
        return html.Append("</main></body></html>").ToString();
    }

    private static void AssertEqual(string implementation, List<SectionRow> expected, List<SectionRow> actual)
    {
        if (actual.Count != expected.Count)
        {
            throw new InvalidOperationException(
                $"Row-scale {implementation} produced {actual.Count} rows; expected {expected.Count}."
            );
        }
        for (var index = 0; index < expected.Count; index++)
        {
            if (actual[index] != expected[index])
            {
                throw new InvalidOperationException(
                    $"Row-scale {implementation} differs at row {index}; "
                        + $"expected {expected[index]}, actual {actual[index]}."
                );
            }
        }
    }

    public readonly record struct SectionRow(string Key, string Heading, string Link);
}
#endif
