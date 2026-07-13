#if NET10_0
using System.Text;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.CompactPrototype;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[MemoryDiagnoser]
[GcServer(true)]
public class FullFlowBenchmark
{
    private static readonly HtmlParserOptions Options = new()
    {
        SkipComments = true,
        SkipProcessingInstructions = true,
        IsKeepingSourceReferences = false,
    };

    private readonly HtmlParser _angleSharpParser = new(Options);
    private readonly HtmlParser _readOnlyParser = new(
        Options,
        ReadOnlyParser.CreateContext(ReadOnlyMetadataProfile.Minimal)
    );
    private readonly CompactParserSession _arenaParser = new(parserOptions: Options);

    private string _html = null!;

    [Params(200, 2000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _html = Bake(Rows);

        var angle = AngleSharp().Count;
        var readOnly = ReadOnly().Count;
        var arenaScalar = ArenaScalar().Count;
        var arenaSimd = ArenaSimd().Count;
        if (angle != Rows || readOnly != Rows || arenaScalar != Rows || arenaSimd != Rows)
        {
            throw new InvalidOperationException(
                $"event count disagrees: expected={Rows}, angleSharp={angle}, readOnly={readOnly}, "
                    + $"arenaScalar={arenaScalar}, arenaSimd={arenaSimd}."
            );
        }
    }

    [Benchmark(Baseline = true)]
    public List<Event> AngleSharp()
    {
        using var document = _angleSharpParser.ParseDocument(_html);
        var events = new List<Event>();
        foreach (var row in document.QuerySelectorAll("#history > tbody > tr"))
        {
            var cells = row.Children;
            events.Add(
                new Event
                {
                    Location = cells.Length > 0 ? cells[0].TextContent : null,
                    Status = cells.Length > 1 ? cells[1].TextContent : null,
                    Date = cells.Length > 2 ? cells[2].TextContent : null,
                }
            );
        }
        return events;
    }

    [Benchmark]
    public List<Event> ReadOnly()
    {
        using var document = _readOnlyParser.ParseReadOnlyDocument(_html);
        var events = new List<Event>();
        var tbody = document.QueryOne(e => e.Id("history"), e => e.Tag("tbody"));
        if (tbody is not null)
        {
            foreach (var row in tbody.QueryAll(e => e.Tag("tr")))
            {
                var @event = new Event();
                var column = 0;
                foreach (var cell in row.QueryAll(e => e.Tag("td")))
                    Assign(@event, column++, cell.GetTextContent());
                events.Add(@event);
            }
        }
        return events;
    }

    [Benchmark]
    public List<Event> ArenaScalar()
    {
        using var document = _arenaParser.Parse(_html);
        var trId = document.Name("tr");
        var builder = new StringBuilder();
        var events = new List<Event>();
        foreach (var node in document.Descendants())
        {
            if (node.Is(trId))
                events.Add(ReadRow(node, builder));
        }
        return events;
    }

    [Benchmark]
    public List<Event> ArenaSimd()
    {
        using var document = _arenaParser.Parse(_html);
        var builder = new StringBuilder();
        var events = new List<Event>();
        foreach (var row in document.Elements("tr"))
            events.Add(ReadRow(row, builder));
        return events;
    }

    private static Event ReadRow(Node row, StringBuilder builder)
    {
        var @event = new Event();
        var column = 0;
        foreach (var cell in row.Children())
        {
            if (!cell.IsElement)
                continue;
            builder.Clear();
            cell.AppendText(builder);
            Assign(@event, column++, builder.ToString());
        }
        return @event;
    }

    private static void Assign(Event @event, int column, string value)
    {
        switch (column)
        {
            case 0:
                @event.Location = value;
                break;
            case 1:
                @event.Status = value;
                break;
            case 2:
                @event.Date = value;
                break;
        }
    }

    private static string Bake(int rows)
    {
        var builder = new StringBuilder();
        builder.Append("<!doctype html><html><head><title>Tracking</title></head><body>");
        builder.Append("<header><nav><ul><li>Home</li><li>Track</li></ul></nav></header>");
        builder.Append("<main><h1>Parcel history</h1><table id=\"history\"><tbody>");
        for (var i = 0; i < rows; i++)
        {
            builder
                .Append("<tr><td>Depot ")
                .Append(i % 37)
                .Append("</td><td>")
                .Append((i % 5) == 0 ? "Delivered" : "In Transit")
                .Append("</td><td>2026-01-")
                .Append((i % 28) + 1)
                .Append("</td></tr>");
        }
        builder.Append("</tbody></table></main></body></html>");
        return builder.ToString();
    }
}
#endif
