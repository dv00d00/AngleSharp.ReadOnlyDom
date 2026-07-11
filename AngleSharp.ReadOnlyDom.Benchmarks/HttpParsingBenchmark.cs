using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.Io;
using AngleSharp.ReadOnlyDom.Filters;
using AngleSharp.ReadOnlyDom.Helpers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using HttpMethod = System.Net.Http.HttpMethod;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[Config(typeof(Config))]
[MemoryDiagnoser]
public class HttpParsingBenchmark
{
    private class Config : ManualConfig
    {
        public Config()
        {
            AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance));
        }
    }

    [Params("15895974334")]
    public string Id { get; set; }

    private static string GetUrl(string id) => $"https://bluedart.com/web/guest/trackdartresult?trackFor=0&trackNo={id}";

    [GlobalSetup]
    public void Setup()
    {
        HtmlEntityProvider.Resolver.GetSymbol("test");
        MimeTypeNames.FromExtension(".txt");
    }

    [Benchmark(Baseline = true)]
    public async Task<List<Event>?> Old()
    {
        var events = await ParseSiteOld(Id!);
        if (events != null)
        {
            foreach (var @event in events)
            {
                Console.WriteLine($"{@event.Date} | {@event.Status} | {@event.Location}");
            }
        }
        return events;
    }

    [Benchmark]
    public async Task<List<Event>?> CustomLibLevel()
    {
        var events = await ParseSiteReadOnly(Id!);
        if (events != null)
        {
            foreach (var @event in events)
            {
                Console.WriteLine($"{@event.Date} | {@event.Status} | {@event.Location}");
            }
        }

        return events;
    }

    private static readonly HttpClient Client = new HttpClient(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });

    private static readonly IBrowsingContext Context = BrowsingContext.New(ReadOnlyParser.DefaultConfig);
    private static ReadOnlySpan<char> ID => "id";
    private static ReadOnlySpan<char> CLASS => "class";
    private static HtmlParserOptions Options = new()
    {
        IsNotConsumingCharacterReferences = true,
        IsNotSupportingFrames = true,
        SkipScriptText = true,
        SkipRawText = true,
        SkipDataText = false,
        SkipComments = true,
        SkipPlaintext = true,
        SkipCDATA = true,
        SkipRCDataText = true,
        SkipProcessingInstructions = true,
        DisableElementPositionTracking = true,
        ShouldEmitAttribute = static (ref StructHtmlToken t, ReadOnlyMemory<char> n) =>
        {
            if (t.Name != "div")
                return false;
            
            var s = n.Span;
            return s.Length switch
            {
                2 => s.SequenceEqual(ID),
                5 => s.SequenceEqual(CLASS),
                _ => false
            };
        },
    };

    static readonly HtmlParser Parser = new(Options, Context);

    private static async Task<List<Event>?> ParseSiteReadOnly(string id)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, GetUrl(id));
        using var lease = await Client.DownloadChars(request);
        var filter = new OnlyElementWithIdAndDescendants("div", $"AWB{id}");
        using var doc = Parser.ParseReadOnlyDocument(lease.Data, lease.RequestedLength, filter.Loop);

        var tbody = doc.QueryOne(
            e => e.TagId("div", $"AWB{id}"),
            e => e.TagId("div", $"SCAN{id}"),
            e => e.Tag("table"),
            e => e.Tag("tbody"));

        var rows = tbody?
            .QueryAll(e => e.Tag("tr"))
            .Select(row => row
                .QueryAll(e => e.Tag("td"))
                .Select(it => it.GetTextContent(trim: TrimMode.TextNodes))
                .ToList()
            )
            .SkipLast(1);

        if (rows == null)
        {
            if (doc
                .QueryOne(e => e.Id("errorDetails"), e => e.TagClass("div", "alert"))
                ?.GetTextContent(trim: TrimMode.TextNodes)
                .Contains("Records Not Found", StringComparison.OrdinalIgnoreCase) == true)
            {
                Console.WriteLine("Not found");
                return null;
            }

            Console.WriteLine("Parser error");
            return null;
        }

        var history = rows
            .Where(it => !it.TryAt(1).IsNullOrWhiteSpace())
            .Select(row =>
            {
                var location = row.TryAt(0);
                var status = row.TryAt(1);
                var rawDate = $"{row.TryAt(2)} {row.TryAt(3)}";

                return new Event
                {
                    Location = location,
                    Status = status,
                    Date = rawDate,
                };
            })
            .ToList();

        var stage =
            history.FirstOrDefault()?.Status?.ContainsI("Delivered") == true
                ? "Stage.Delivered"
                : "Stage.Transit";

        Console.WriteLine(stage);

        return history;
    }
    
    private static async Task<List<Event>?> ParseSiteOld(string id)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, GetUrl(id));

        var response = await Client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);

        var div = doc.QuerySelector($"div#AWB{id}");
        var table = div?.QuerySelector<IHtmlTableElement>($"div#SCAN{id} table");
        var rows = table?.Bodies.FirstOrDefault()?
            .Rows
            .Select(row => row.Cells.Select(it => it.TextContent.Trim()).ToList())
            .SkipLast(1)
            .ToList();

        if (rows == null)
        {
            if (doc.QuerySelector("#errorDetails > div.alert")?.TextContent
                    .Contains("Records Not Found", StringComparison.OrdinalIgnoreCase) == true)
            {
                Console.WriteLine("Not found");
                return null;
            }

            Console.WriteLine("Parser error");
            return null;
        }

        var history = rows
            .Where(it => !string.IsNullOrWhiteSpace(it.ElementAtOrDefault(1)))
            .Select(row =>
            {
                var rawDate = $"{row.ElementAtOrDefault(2)} {row.ElementAtOrDefault(3)}";
                var status = row.ElementAtOrDefault(1)!;
                var location = row.ElementAtOrDefault(0);

                return new Event
                {
                    Location = location,
                    Status = status,
                    Date = rawDate,
                };
            }).ToList();

        var stage =
            history.FirstOrDefault()?.Status?.Contains("Delivered", StringComparison.OrdinalIgnoreCase) == true
                ? "Stage.Delivered"
                : "Stage.Transit";

        Console.WriteLine(stage);

        return history;
    }
}
