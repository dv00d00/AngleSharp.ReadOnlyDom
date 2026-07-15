#if NET10_0
using System.Diagnostics;
using System.Net;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.Compact;
using AngleSharp.ReadOnlyDom.Html;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

internal static class QueryWorkloadRunner
{
    public static int Run(string[] args)
    {
        var iterations = GetPositiveInt(args, "--iterations", 30);
        var output = GetOption(args, "--output");
        var workloads = Workload.CreateAll();
        var engines = new Engine[]
        {
            new("AngleSharp core", static workload => new StandardSession(workload)),
            new("Read-only DOM", static workload => new ReadOnlySession(workload)),
            new("Compact arena", static workload => new CompactSession(workload)),
            new("Streaming lower bound", static workload => new StreamingSession(workload)),
        };

        var measurements = new List<Measurement>();
        var structures = new List<Structure>();
        foreach (var workload in workloads)
        {
            structures.Add(MeasureStructure(workload));
            string? expected = null;
            foreach (var engine in engines)
            {
                QueryResult result;
                using (var validation = engine.Create(workload))
                    result = validation.Execute();
                expected ??= result.Output;
                var matchesOracle = result.Output == expected;
                measurements.Add(Measure(engine, workload, iterations, result, matchesOracle));
            }
        }

        var report = Render(iterations, workloads, structures, measurements);
        if (output is null)
        {
            Console.WriteLine(report);
        }
        else
        {
            var path = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, report);
            Console.WriteLine(path);
        }
        return 0;
    }

    private static Measurement Measure(
        Engine engine,
        Workload workload,
        int iterations,
        QueryResult validation,
        bool matchesOracle
    )
    {
        using (var warmup = engine.Create(workload))
            _ = warmup.Execute();

        ForceCollection();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            using var session = engine.Create(workload);
            var result = session.Execute();
            GC.KeepAlive(result.Output);
        }
        stopwatch.Stop();
        var allocated = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / iterations;

        double queryMicroseconds;
        long queryAllocated;
        using (var querySession = engine.Create(workload))
        {
            _ = querySession.Execute();
            var queryAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var queryStopwatch = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++)
            {
                var result = querySession.Execute();
                GC.KeepAlive(result.Output);
            }
            queryStopwatch.Stop();
            queryAllocated = (GC.GetAllocatedBytesForCurrentThread() - queryAllocatedBefore) / iterations;
            queryMicroseconds = queryStopwatch.Elapsed.TotalMicroseconds / iterations;
        }

        ForceCollection();
        var retainedBefore = GC.GetTotalMemory(true);
        using var retainedSession = engine.Create(workload);
        var peak = Math.Max(retainedBefore, GC.GetTotalMemory(false));
        var retained = Math.Max(0, GC.GetTotalMemory(true) - retainedBefore);
        peak = Math.Max(peak, GC.GetTotalMemory(false));

        var copyAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var escapingCopy = new string(validation.Output.AsSpan());
        var outputAllocated = GC.GetAllocatedBytesForCurrentThread() - copyAllocatedBefore;
        GC.KeepAlive(escapingCopy);

        return new Measurement(
            workload.Name,
            engine.Name,
            stopwatch.Elapsed.TotalMicroseconds / iterations,
            allocated,
            queryMicroseconds,
            queryAllocated,
            retained,
            Math.Max(0, peak - retainedBefore),
            outputAllocated,
            validation.Output.Length,
            validation.Counters,
            matchesOracle
        );
    }

    private static Structure MeasureStructure(Workload workload)
    {
        using var document = new HtmlParser().ParseDocument(workload.Html);
        var depths = new List<int>();
        var subtreeNodes = new List<int>();
        var subtreeText = new List<int>();
        var valueCount = 0;
        Visit(document, 0);
        depths.Sort();
        subtreeNodes.Sort();
        subtreeText.Sort();
        return new Structure(
            workload.Name,
            depths.Count,
            depths.Count == 0 ? 0 : depths[^1],
            Percentile(depths, 0.95),
            Percentile(subtreeNodes, 0.50),
            Percentile(subtreeNodes, 0.95),
            Percentile(subtreeText, 0.50),
            Percentile(subtreeText, 0.95),
            valueCount,
            Count(workload.Html, '&')
        );

        (int Nodes, int Text) Visit(INode node, int depth)
        {
            depths.Add(depth);
            var nodes = 1;
            var text = node is IText value ? value.Data.Length : 0;
            if (node is IText)
                valueCount++;
            if (node is IElement element)
                valueCount += element.Attributes.Length;
            var children = node is IHtmlTemplateElement template ? template.Content.ChildNodes : node.ChildNodes;
            foreach (var child in children)
            {
                var childResult = Visit(child, depth + 1);
                nodes += childResult.Nodes;
                text += childResult.Text;
            }
            subtreeNodes.Add(nodes);
            subtreeText.Add(text);
            return (nodes, text);
        }
    }

    private static string Render(
        int iterations,
        IReadOnlyList<Workload> workloads,
        IReadOnlyList<Structure> structures,
        IReadOnlyList<Measurement> measurements
    )
    {
        var report = new StringBuilder();
        report.AppendLine("# Query-directed workload report").AppendLine();
        report.AppendLine($"- Commit: `{GetCommit()}`");
        report.AppendLine($"- Runtime: `{RuntimeInformation.FrameworkDescription}`");
        report.AppendLine($"- OS: `{RuntimeInformation.OSDescription}`");
        report.AppendLine($"- GC: `{(GCSettings.IsServerGC ? "Server" : "Workstation")}`");
        report.AppendLine($"- Iterations: `{iterations}` per engine/workload");
        report.AppendLine(
            "- Time and total allocation cover parse, query, escaping output, and disposal. Retained bytes are incremental managed heap after parse and forced GC; pooled backing arrays already present in shared pools are excluded, so compact retained size is a lower bound. Peak live bytes are sampled and approximate."
        );
        report.AppendLine(
            "- Output allocation is the measured cost of one escaping UTF-16 string copy. Logical counters describe the query implementation; selector internals that are opaque are conservatively counted as a full document scan."
        );
        report.AppendLine(
            "- Decoded-value rate uses source entity markers divided by parsed attribute and text values as a reproducible corpus-level proxy."
        );
        report.AppendLine(
            "- The streaming implementation is a deliberately limited well-formed-input lower bound, not a correctness candidate. AngleSharp core is the oracle."
        );
        report.AppendLine(
            "- Oracle mismatches are retained in the report as findings so the pathological corpus can expose semantic gaps without suppressing the performance baseline."
        );
        report.AppendLine();

        report.AppendLine("## Workloads").AppendLine();
        report.AppendLine("| Workload | Query | Input | Pathological |");
        report.AppendLine("| --- | --- | ---: | :---: |");
        foreach (var workload in workloads)
            report.AppendLine(
                $"| {workload.Name} | {workload.Description} | {workload.Html.Length:N0} chars | {(workload.Pathological ? "yes" : "no")} |"
            );
        report.AppendLine();

        report.AppendLine("## Structure").AppendLine();
        report.AppendLine(
            "| Workload | Nodes | Max depth | P95 depth | Median subtree nodes | P95 subtree nodes | Median subtree text | P95 subtree text | Decoded-value rate |"
        );
        report.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var value in structures)
            report.AppendLine(
                $"| {value.Workload} | {value.Nodes:N0} | {value.MaxDepth:N0} | {value.P95Depth:N0} | {value.MedianSubtreeNodes:N0} | {value.P95SubtreeNodes:N0} | {value.MedianSubtreeText:N0} | {value.P95SubtreeText:N0} | {Rate(value.EntityMarkers, value.ValueCount)} ({value.EntityMarkers:N0}/{value.ValueCount:N0}) |"
            );
        report.AppendLine();

        report.AppendLine("## Repeated-query measurements").AppendLine();
        report.AppendLine("| Workload | Engine | Query-only mean | Query-only allocation |");
        report.AppendLine("| --- | --- | ---: | ---: |");
        foreach (var value in measurements)
            report.AppendLine(
                $"| {value.Workload} | {value.Engine} | {value.QueryMicroseconds:N1} us | {Bytes(value.QueryAllocated)} |"
            );
        report.AppendLine();

        report.AppendLine("## Reuse break-even").AppendLine();
        report.AppendLine(
            "Estimated from `parse = end-to-end - query-only` and `total(N) = parse + N * query`. Values compare compact arena with read-only DOM on one retained document."
        );
        report.AppendLine();
        report.AppendLine("| Workload | Compact beats ROD at | ROD parse estimate | Compact parse estimate |");
        report.AppendLine("| --- | ---: | ---: | ---: |");
        foreach (var workload in workloads)
        {
            var readOnly = measurements.Single(value =>
                value.Workload == workload.Name && value.Engine == "Read-only DOM"
            );
            var compact = measurements.Single(value =>
                value.Workload == workload.Name && value.Engine == "Compact arena"
            );
            report.AppendLine(
                $"| {workload.Name} | {BreakEven(readOnly, compact)} | {Math.Max(0, readOnly.Microseconds - readOnly.QueryMicroseconds):N1} us | {Math.Max(0, compact.Microseconds - compact.QueryMicroseconds):N1} us |"
            );
        }
        report.AppendLine();

        report.AppendLine("## End-to-end measurements").AppendLine();
        report.AppendLine(
            "| Workload | Engine | Mean | Allocated | Incremental retained | Approx. peak live | Output allocation | Output chars | Oracle match |"
        );
        report.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | :---: |");
        foreach (var value in measurements)
            report.AppendLine(
                $"| {value.Workload} | {value.Engine} | {value.Microseconds:N1} us | {Bytes(value.Allocated)} | {Bytes(value.Retained)} | {Bytes(value.PeakLive)} | {Bytes(value.OutputAllocated)} | {value.OutputChars:N0} | {(value.MatchesOracle ? "yes" : "no")} |"
            );
        report.AppendLine();

        report.AppendLine("## Query counters").AppendLine();
        report.AppendLine(
            "| Workload | Engine | Nodes inspected | Attributes inspected | Text nodes inspected | Nodes retained | Attributes retained | Input consumed | Decoded values | Retained / inspected |"
        );
        report.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var value in measurements)
        {
            var counters = value.Counters;
            var fraction =
                counters.NodesInspected == 0 ? "n/a" : $"{counters.NodesRetained / (double)counters.NodesInspected:P2}";
            report.AppendLine(
                $"| {value.Workload} | {value.Engine} | {counters.NodesInspected:N0} | {counters.AttributesInspected:N0} | {counters.TextNodesInspected:N0} | {counters.NodesRetained:N0} | {counters.AttributesRetained:N0} | {counters.InputConsumed:N0} | {counters.DecodedValues:N0} | {fraction} |"
            );
        }
        report.AppendLine();

        report.AppendLine("## Recommendation").AppendLine();
        report.AppendLine(
            "The first integrated prototype should be only `first div#content -> normalized subtree text`, not a DSL or general extraction architecture. Implement it directly over the tokenizer with an explicit result type and AngleSharp core as the correctness oracle; add product cards and head/body extraction only after that gate. The deliberately limited streaming lower bound shows the available ceiling, while its pathological mismatch shows why it cannot become production code unchanged."
        );
        report.AppendLine();
        report.AppendLine(
            "Keep the compact arena as the reusable-document option: it reduces repeated-query cost and allocation, and the break-even table shows when its higher construction cost is recovered. Compact and read-only DOM both match AngleSharp core on the pathological extraction; the deliberately limited streaming scanner remains intentionally non-conforming."
        );
        report.AppendLine();

        report.AppendLine("## Reproduce").AppendLine();
        report.AppendLine("```powershell");
        report.AppendLine("./scripts/bench.ps1 query");
        report.AppendLine("# Fast correctness and wiring check:");
        report.AppendLine(
            "dotnet run --project ./benchmarks/AngleSharp.ReadOnlyDom.Benchmarks -c Release -f net10.0 -- --query-workloads --iterations 1"
        );
        report.AppendLine("```");
        return report.ToString();
    }

    private static string BreakEven(Measurement baseline, Measurement candidate)
    {
        var baselineParse = Math.Max(0, baseline.Microseconds - baseline.QueryMicroseconds);
        var candidateParse = Math.Max(0, candidate.Microseconds - candidate.QueryMicroseconds);
        if (candidateParse + candidate.QueryMicroseconds <= baselineParse + baseline.QueryMicroseconds)
            return "1 query";
        var perQuerySaving = baseline.QueryMicroseconds - candidate.QueryMicroseconds;
        if (perQuerySaving <= 0)
            return "never";
        var queries = Math.Max(1, (int)Math.Ceiling((candidateParse - baselineParse) / perQuerySaving));
        return $"{queries:N0} queries";
    }

    private static string Rate(int numerator, int denominator) =>
        denominator == 0 ? "n/a" : $"{numerator / (double)denominator:P2}";

    private static int Percentile(IReadOnlyList<int> values, double percentile) =>
        values.Count == 0 ? 0 : values[Math.Min(values.Count - 1, (int)Math.Floor((values.Count - 1) * percentile))];

    private static int Count(string value, char character)
    {
        var count = 0;
        foreach (var item in value)
            if (item == character)
                count++;
        return count;
    }

    private static string GetCommit()
    {
        try
        {
            using var process = Process.Start(
                new ProcessStartInfo("git", "rev-parse --short HEAD")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            )!;
            var commit = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 ? commit : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string Bytes(long value) => $"{value / 1024d:N2} KB";

    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static int GetPositiveInt(string[] args, string name, int fallback)
    {
        var option = GetOption(args, name);
        return option is null ? fallback
            : int.TryParse(option, out var value) && value > 0 ? value
            : throw new ArgumentOutOfRangeException(name);
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private sealed record Engine(string Name, Func<Workload, IQuerySession> Create);

    private interface IQuerySession : IDisposable
    {
        QueryResult Execute();
    }

    private sealed record Workload(string Name, string Description, QueryKind Query, string Html, bool Pathological)
    {
        public static Workload[] CreateAll() =>
            [
                new(
                    "Content text",
                    "first div#content -> normalized subtree text",
                    QueryKind.Content,
                    BuildContentPage(),
                    false
                ),
                new(
                    "Product cards",
                    "article.product -> sku, name, price, href",
                    QueryKind.Products,
                    BuildProductPage(),
                    false
                ),
                new(
                    "Head and body",
                    "title + meta description + first h1 + normalized body text",
                    QueryKind.HeadBody,
                    BuildHeadBodyPage(),
                    false
                ),
                new(
                    "Adversarial content",
                    "first div#content over foster parenting, formatting adoption, template, and entities",
                    QueryKind.Content,
                    BuildAdversarialPage(),
                    true
                ),
            ];

        private static string BuildContentPage()
        {
            var html = new StringBuilder("<!doctype html><html><body><nav>outside</nav>");
            for (var i = 0; i < 80; i++)
                html.Append("<aside><span>noise ").Append(i).Append("</span></aside>");
            html.Append("<div id=\"content\"><h1>Release &amp; status</h1>");
            for (var i = 0; i < 120; i++)
                html.Append("<p>Row ").Append(i).Append(" has <b>useful</b> text.</p>");
            return html.Append("</div><footer>outside</footer></body></html>").ToString();
        }

        private static string BuildProductPage()
        {
            var html = new StringBuilder("<!doctype html><html><body><main>");
            for (var i = 0; i < 160; i++)
            {
                html.Append("<article class=\"product card\" data-sku=\"SKU-")
                    .Append(i)
                    .Append("\"><h2>Product ")
                    .Append(i)
                    .Append(" &amp; pack</h2><span class=\"price\">£")
                    .Append(i + 10)
                    .Append(".99</span><a href=\"/p/")
                    .Append(i)
                    .Append("\">Details</a></article>");
            }
            return html.Append("</main></body></html>").ToString();
        }

        private static string BuildHeadBodyPage()
        {
            var html = new StringBuilder(
                "<!doctype html><html><head><title>Example &amp; report</title><meta name=\"description\" content=\"Measured extraction\"></head><body><h1>Daily report</h1>"
            );
            for (var i = 0; i < 180; i++)
                html.Append("<section><h2>Section ")
                    .Append(i)
                    .Append("</h2><p>Body value ")
                    .Append(i)
                    .Append(".</p></section>");
            return html.Append("</body></html>").ToString();
        }

        private static string BuildAdversarialPage() =>
            "<!doctype html><html><body><table><tr><td>cell<div id=content><b>one<i> &amp; two</b> three</i>"
            + "<template><p>template text</p></template></td></tr>foster text</table><p>after";
    }

    private enum QueryKind
    {
        Content,
        Products,
        HeadBody,
    }

    private readonly record struct QueryResult(string Output, Counters Counters);

    private record struct Counters(
        int NodesInspected,
        int AttributesInspected,
        int TextNodesInspected,
        int NodesRetained,
        int AttributesRetained,
        int InputConsumed,
        int DecodedValues
    );

    private readonly record struct Measurement(
        string Workload,
        string Engine,
        double Microseconds,
        long Allocated,
        double QueryMicroseconds,
        long QueryAllocated,
        long Retained,
        long PeakLive,
        long OutputAllocated,
        int OutputChars,
        Counters Counters,
        bool MatchesOracle
    );

    private readonly record struct Structure(
        string Workload,
        int Nodes,
        int MaxDepth,
        int P95Depth,
        int MedianSubtreeNodes,
        int P95SubtreeNodes,
        int MedianSubtreeText,
        int P95SubtreeText,
        int ValueCount,
        int EntityMarkers
    );

    private sealed class StandardSession : IQuerySession
    {
        private readonly Workload _workload;
        private readonly IDocument _document;

        public StandardSession(Workload workload)
        {
            _workload = workload;
            _document = new HtmlParser().ParseDocument(workload.Html);
        }

        public QueryResult Execute() => WorkloadQueries.Standard(_workload, _document);

        public void Dispose() => _document.Dispose();
    }

    private sealed class ReadOnlySession : IQuerySession
    {
        private readonly Workload _workload;
        private readonly IReadOnlyDocument _document;

        public ReadOnlySession(Workload workload)
        {
            _workload = workload;
            _document = ReadOnlyParser
                .CreateParser(ReadOnlyMetadataProfile.Minimal)
                .ParseReadOnlyDocument(workload.Html);
        }

        public QueryResult Execute() => WorkloadQueries.ReadOnly(_workload, _document);

        public void Dispose() => _document.Dispose();
    }

    private sealed class CompactSession : IQuerySession
    {
        private readonly Workload _workload;
        private readonly CompactDocument _document;

        public CompactSession(Workload workload)
        {
            _workload = workload;
            _document = CompactParser.CreateParser().ParseCompactDocument(workload.Html);
        }

        public QueryResult Execute() => WorkloadQueries.Compact(_workload, _document);

        public void Dispose() => _document.Dispose();
    }

    private sealed class StreamingSession(Workload workload) : IQuerySession
    {
        public QueryResult Execute() => StreamingBaseline.Execute(workload);

        public void Dispose() { }
    }

    private static class WorkloadQueries
    {
        public static QueryResult Standard(Workload workload, IDocument document)
        {
            var counts = Count(document);
            var output = workload.Query switch
            {
                QueryKind.Content => Normalize(document.QuerySelector("div#content")?.TextContent),
                QueryKind.Products => Products(
                    document.QuerySelectorAll("article.product"),
                    static card => card.GetAttribute("data-sku"),
                    static card => card.QuerySelector("h2")?.TextContent,
                    static card => card.QuerySelector(".price")?.TextContent,
                    static card => card.QuerySelector("a")?.GetAttribute("href")
                ),
                QueryKind.HeadBody => Join(
                    document.Title,
                    document.QuerySelector("meta[name=description]")?.GetAttribute("content"),
                    document.QuerySelector("h1")?.TextContent,
                    document.Body?.TextContent
                ),
                _ => throw new ArgumentOutOfRangeException(),
            };
            counts.NodesRetained = RetainedNodes(workload.Query, document.QuerySelectorAll("article.product").Length);
            counts.AttributesRetained =
                workload.Query is QueryKind.Products ? counts.NodesRetained * 2
                : workload.Query is QueryKind.HeadBody ? 1
                : 0;
            counts.InputConsumed = workload.Html.Length;
            counts.DecodedValues = QueryWorkloadRunner.Count(workload.Html, '&');
            return new QueryResult(output, counts);
        }

        public static QueryResult ReadOnly(Workload workload, IReadOnlyDocument document)
        {
            var counts = Count(document);
            string output;
            switch (workload.Query)
            {
                case QueryKind.Content:
                    output = Normalize(
                        document.QueryOne(static node => node.TagId("div", "content"))?.GetTextContent()
                    );
                    counts.NodesRetained = output.Length == 0 ? 0 : 1;
                    break;
                case QueryKind.Products:
                    var cards = document
                        .AllDescendants()
                        .OfType<IReadOnlyElement>()
                        .Where(static node => node.TagClass("article", "product"))
                        .ToArray();
                    output = Products(
                        cards,
                        static card => card.Attributes["data-sku"]?.Value.ToString(),
                        static card => card.QueryOne(static node => node.Tag("h2"))?.GetTextContent(),
                        static card => card.QueryOne(static node => node.Class("price"))?.GetTextContent(),
                        static card =>
                            (card.QueryOne(static node => node.Tag("a")) as IReadOnlyElement)
                                ?.Attributes["href"]
                                ?.Value.ToString()
                    );
                    counts.NodesRetained = cards.Length;
                    counts.AttributesRetained = cards.Length * 2;
                    break;
                case QueryKind.HeadBody:
                    var meta =
                        document.QueryOne(static node => node.Tag("meta") && node.Attr("name", "description"))
                        as IReadOnlyElement;
                    output = Join(
                        document.QueryOne(static node => node.Tag("title"))?.GetTextContent(),
                        meta?.Attributes["content"]?.Value.ToString(),
                        document.QueryOne(static node => node.Tag("h1"))?.GetTextContent(),
                        document.QueryOne(static node => node.Tag("body"))?.GetTextContent()
                    );
                    counts.NodesRetained = 4;
                    counts.AttributesRetained = 1;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            counts.InputConsumed = workload.Html.Length;
            counts.DecodedValues = QueryWorkloadRunner.Count(workload.Html, '&');
            return new QueryResult(output, counts);
        }

        public static QueryResult Compact(Workload workload, CompactDocument document)
        {
            var counts = Count(document);
            string output;
            switch (workload.Query)
            {
                case QueryKind.Content:
                    var content = document.Elements("div").WithAttribute("id", "content").First();
                    output = content.Exists ? Normalize(content.Text()) : string.Empty;
                    counts.NodesRetained = content.Exists ? 1 : 0;
                    break;
                case QueryKind.Products:
                    var cards = new List<Compact.Node>();
                    foreach (var card in document.Elements("article").WithClass("product"))
                        cards.Add(card);
                    output = Products(
                        cards,
                        static card => card.Attr("data-sku").ToString(),
                        static card => card.Elements("h2").First().Text(),
                        static card => Find(card, static node => node.HasClass("price")).Text(),
                        static card => card.Elements("a").First().Attr("href").ToString()
                    );
                    counts.NodesRetained = cards.Count;
                    counts.AttributesRetained = cards.Count * 2;
                    break;
                case QueryKind.HeadBody:
                    var meta = document.Elements("meta").WithAttribute("name", "description").First();
                    var title = document.Elements("title").First();
                    var h1 = document.Elements("h1").First();
                    var body = document.Elements("body").First();
                    output = Join(
                        title.Exists ? title.Text() : null,
                        meta.Exists ? meta.Attr("content").ToString() : null,
                        h1.Exists ? h1.Text() : null,
                        body.Exists ? body.Text() : null
                    );
                    counts.NodesRetained = 4;
                    counts.AttributesRetained = 1;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            counts.InputConsumed = workload.Html.Length;
            counts.DecodedValues = QueryWorkloadRunner.Count(workload.Html, '&');
            return new QueryResult(output, counts);
        }

        private static Compact.Node Find(Compact.Node root, Func<Compact.Node, bool> predicate)
        {
            foreach (var descendant in root.Descendants())
                if (predicate(descendant))
                    return descendant;
            return default;
        }

        private static Counters Count(INode node)
        {
            var counters = new Counters();
            Visit(node);
            return counters;

            void Visit(INode current)
            {
                counters.NodesInspected++;
                if (current is IElement element)
                    counters.AttributesInspected += element.Attributes.Length;
                if (current is IText)
                    counters.TextNodesInspected++;
                var children = current is IHtmlTemplateElement template
                    ? template.Content.ChildNodes
                    : current.ChildNodes;
                foreach (var child in children)
                    Visit(child);
            }
        }

        private static Counters Count(IReadOnlyNode node)
        {
            var counters = new Counters();
            Visit(node);
            return counters;

            void Visit(IReadOnlyNode current)
            {
                counters.NodesInspected++;
                if (current is IReadOnlyElement element)
                    counters.AttributesInspected += element.Attributes.Length;
                if (current is IReadOnlyTextNode)
                    counters.TextNodesInspected++;
                var children = current is IReadOnlyTemplateElement template ? template.Content : current.ChildNodes;
                foreach (var child in children)
                    Visit(child);
            }
        }

        private static Counters Count(CompactDocument document)
        {
            var counters = new Counters
            {
                NodesInspected = document.NodeCount,
                AttributesInspected = document.AttributeCount,
            };
            foreach (var node in document.Descendants())
                if (node.Kind == CompactNodeKind.Text)
                    counters.TextNodesInspected++;
            return counters;
        }

        private static int RetainedNodes(QueryKind query, int products) =>
            query switch
            {
                QueryKind.Content => 1,
                QueryKind.Products => products,
                QueryKind.HeadBody => 4,
                _ => 0,
            };

        private static string Products<T>(
            IEnumerable<T> cards,
            Func<T, string?> sku,
            Func<T, string?> name,
            Func<T, string?> price,
            Func<T, string?> href
        )
        {
            var result = new StringBuilder();
            foreach (var card in cards)
                result
                    .Append(Normalize(sku(card)))
                    .Append('|')
                    .Append(Normalize(name(card)))
                    .Append('|')
                    .Append(Normalize(price(card)))
                    .Append('|')
                    .Append(Normalize(href(card)))
                    .Append('\n');
            return result.ToString();
        }
    }

    private static class StreamingBaseline
    {
        public static QueryResult Execute(Workload workload)
        {
            var extraction = workload.Query switch
            {
                QueryKind.Content => Content(workload.Html),
                QueryKind.Products => (Products(workload.Html), workload.Html.Length),
                QueryKind.HeadBody => (HeadBody(workload.Html), workload.Html.Length),
                _ => (string.Empty, 0),
            };
            var output = extraction.Item1;
            var inspected = workload.Html[..extraction.Item2];
            var tags = Count(inspected, '<');
            var attributes = Count(inspected, '=');
            var retainedNodes = workload.Query switch
            {
                QueryKind.Products => Count(output, '\n'),
                QueryKind.HeadBody => 4,
                _ => output.Length == 0 ? 0 : 1,
            };
            var retainedAttributes = workload.Query switch
            {
                QueryKind.Products => retainedNodes * 2,
                QueryKind.HeadBody => 1,
                _ => 0,
            };
            return new QueryResult(
                output,
                new Counters(
                    tags,
                    attributes,
                    CountTextRuns(inspected),
                    retainedNodes,
                    retainedAttributes,
                    extraction.Item2,
                    Count(inspected, '&')
                )
            );
        }

        private static int CountTextRuns(string html)
        {
            var count = 0;
            var inTag = false;
            var inText = false;
            foreach (var character in html)
            {
                if (character == '<')
                {
                    inTag = true;
                    inText = false;
                }
                else if (character == '>')
                {
                    inTag = false;
                }
                else if (!inTag && !char.IsWhiteSpace(character) && !inText)
                {
                    count++;
                    inText = true;
                }
            }
            return count;
        }

        private static (string Output, int Consumed) Content(string html)
        {
            var start = html.IndexOf("<div id=\"content\">", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                start = html.IndexOf("<div id=content>", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return (string.Empty, html.Length);
            start = html.IndexOf('>', start) + 1;
            var end = html.IndexOf("</div>", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
                end = html.Length;
            var consumed = end == html.Length ? html.Length : end + "</div>".Length;
            return (Text(html.AsSpan(start, end - start)), consumed);
        }

        private static string Products(string html)
        {
            var output = new StringBuilder();
            var position = 0;
            while (
                (
                    position = html.IndexOf(
                        "<article class=\"product card\"",
                        position,
                        StringComparison.OrdinalIgnoreCase
                    )
                ) >= 0
            )
            {
                var end = html.IndexOf("</article>", position, StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                    break;
                var card = html.AsSpan(position, end + 10 - position);
                output
                    .Append(Attribute(card, "data-sku"))
                    .Append('|')
                    .Append(Element(card, "h2"))
                    .Append('|')
                    .Append(ElementByClass(card, "span", "price"))
                    .Append('|')
                    .Append(Attribute(ElementMarkup(card, "a"), "href"))
                    .Append('\n');
                position = end + 10;
            }
            return output.ToString();
        }

        private static string HeadBody(string html) =>
            Join(
                Element(html, "title"),
                Attribute(ElementMarkup(html, "meta"), "content"),
                Element(html, "h1"),
                Element(html, "body")
            );

        private static string Element(ReadOnlySpan<char> html, string tag)
        {
            var markup = ElementMarkup(html, tag);
            if (markup.IsEmpty)
                return string.Empty;
            var open = markup.IndexOf('>');
            var close = markup.LastIndexOf("</".AsSpan());
            return open < 0 || close <= open ? string.Empty : Text(markup[(open + 1)..close]);
        }

        private static string ElementByClass(ReadOnlySpan<char> html, string tag, string className)
        {
            var search = $"<{tag}";
            var offset = 0;
            while (offset < html.Length)
            {
                var relative = html[offset..].IndexOf(search, StringComparison.OrdinalIgnoreCase);
                if (relative < 0)
                    return string.Empty;
                var start = offset + relative;
                var markup = ElementMarkup(html[start..], tag);
                if (Attribute(markup, "class").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(className))
                    return Element(markup, tag);
                offset = start + search.Length;
            }
            return string.Empty;
        }

        private static ReadOnlySpan<char> ElementMarkup(ReadOnlySpan<char> html, string tag)
        {
            var start = html.IndexOf($"<{tag}", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return default;
            var openEnd = html[start..].IndexOf('>');
            if (openEnd < 0)
                return default;
            if (html[start..(start + openEnd + 1)].TrimEnd().EndsWith("/>", StringComparison.Ordinal))
                return html[start..(start + openEnd + 1)];
            var closing = $"</{tag}>";
            var close = html[(start + openEnd + 1)..].IndexOf(closing, StringComparison.OrdinalIgnoreCase);
            return close < 0 ? html[start..] : html[start..(start + openEnd + 1 + close + closing.Length)];
        }

        private static string Attribute(ReadOnlySpan<char> markup, string name)
        {
            var start = markup.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return string.Empty;
            var equals = markup[start..].IndexOf('=');
            if (equals < 0)
                return string.Empty;
            var value = markup[(start + equals + 1)..].TrimStart();
            if (value.IsEmpty)
                return string.Empty;
            var quote = value[0];
            if (quote is '\'' or '"')
            {
                value = value[1..];
                var end = value.IndexOf(quote);
                return WebUtility.HtmlDecode((end < 0 ? value : value[..end]).ToString());
            }
            var length = value.IndexOfAny(' ', '>', '/');
            return WebUtility.HtmlDecode((length < 0 ? value : value[..length]).ToString());
        }

        private static string Text(ReadOnlySpan<char> markup)
        {
            var text = new StringBuilder(markup.Length);
            var inTag = false;
            foreach (var character in markup)
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
    }

    private static string Join(params string?[] values) => string.Join('|', values.Select(Normalize));

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var result = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = result.Length != 0;
                continue;
            }
            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }
            result.Append(character);
        }
        return result.ToString();
    }
}
#endif
