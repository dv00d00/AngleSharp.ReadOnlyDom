using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.Html;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

internal static class RetainedMemoryRunner
{
    public static int Run(string[] args)
    {
#if NET10_0
        var tier = GetOption(args, "--tier") ?? "small";
        var output = GetOption(args, "--output");
        var repetitions = int.TryParse(GetOption(args, "--repetitions"), out var value) ? value : 3;
        if (repetitions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(repetitions));
        }

        var sources = BenchmarkCorpus.Load(tier);
        var standard = Median(Enumerable.Range(0, repetitions).Select(_ => MeasureStandard(sources)).ToArray());
        var readOnly = Enum.GetValues<ReadOnlyMetadataProfile>()
            .Select(profile =>
                Median(Enumerable.Range(0, repetitions).Select(_ => MeasureReadOnly(sources, profile)).ToArray())
            )
            .ToArray();
        var report = Render(tier, repetitions, sources, standard, readOnly);

        if (output is not null)
        {
            var path = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, report);
            Console.WriteLine(path);
        }
        else
        {
            Console.WriteLine(report);
        }

        return 0;
#else
        Console.Error.WriteLine("Retained-memory measurement is supported only on net10.0.");
        return 2;
#endif
    }

#if NET10_0
    private static Measurement MeasureStandard(IReadOnlyList<CorpusDocument> sources)
    {
        var parser = new HtmlParser();
        var documents = new List<IDocument>(sources.Count);
        return Measure(
            "Standard AngleSharp",
            sources,
            source =>
            {
                var document = parser.ParseDocument(source);
                documents.Add(document);
                return Count(document);
            },
            documents
        );
    }

    private static Measurement MeasureReadOnly(IReadOnlyList<CorpusDocument> sources, ReadOnlyMetadataProfile profile)
    {
        var parser = ReadOnlyParser.CreateParser(profile);
        var documents = new List<IReadOnlyDocument>(sources.Count);
        return Measure(
            $"Read-only {profile}",
            sources,
            source =>
            {
                var document = parser.ParseReadOnlyDocument(source);
                documents.Add(document);
                return Count(document);
            },
            documents
        );
    }

    private static Measurement Measure<T>(
        string implementation,
        IReadOnlyList<CorpusDocument> sources,
        Func<string, Counts> parse,
        List<T> documents
    )
        where T : IDisposable
    {
        ForceCollection();
        var retainedBefore = GC.GetTotalMemory(true);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var peak = retainedBefore;
        var counts = new Counts();
        var stopwatch = Stopwatch.StartNew();

        foreach (var source in sources)
        {
            counts += parse(source.Html);
            peak = Math.Max(peak, GC.GetTotalMemory(false));
        }

        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var retained = Math.Max(0, GC.GetTotalMemory(true) - retainedBefore);
        GC.KeepAlive(documents);
        foreach (var document in documents)
        {
            document.Dispose();
        }

        documents.Clear();
        ForceCollection();
        return new Measurement(implementation, stopwatch.Elapsed, allocated, retained, peak - retainedBefore, counts);
    }

    private static Counts Count(INode node)
    {
        var counts = node switch
        {
            IElement element => new Counts(1, 0, element.Attributes.Length),
            IText => new Counts(0, 1, 0),
            _ => new Counts(),
        };
        foreach (var child in node.ChildNodes)
        {
            counts += Count(child);
        }

        return counts;
    }

    private static Counts Count(IReadOnlyNode node)
    {
        var counts = node switch
        {
            IReadOnlyElement element when node is not IReadOnlyDocument => new Counts(1, 0, element.Attributes.Length),
            IReadOnlyTextNode => new Counts(0, 1, 0),
            _ => new Counts(),
        };
        foreach (var child in node.ChildNodes)
        {
            counts += Count(child);
        }

        return counts;
    }

    private static string Render(
        string tier,
        int repetitions,
        IReadOnlyList<CorpusDocument> sources,
        Measurement standard,
        IReadOnlyList<Measurement> readOnly
    )
    {
        var report = new StringBuilder();
        report.AppendLine("# Retained-memory report").AppendLine();
        report.AppendLine($"- Commit: `{GetCommit()}`");
        report.AppendLine($"- Runtime: `{RuntimeInformation.FrameworkDescription}`");
        report.AppendLine($"- OS: `{RuntimeInformation.OSDescription}`");
        report.AppendLine($"- Corpus: `{tier}` ({sources.Count} checked-in documents)");
        report.AppendLine($"- Repetitions: `{repetitions}` (median reported independently for each metric)");
        report.AppendLine(
            "- Method: sources are rooted before measurement; a forced full GC establishes the baseline; all parsed documents remain reachable; another forced full GC estimates retained managed bytes."
        );
        report.AppendLine(
            "- Noise: run on an idle machine. Peak live heap is sampled after each document and is approximate; total allocation is current-thread allocation and excludes work moved to other threads."
        );
        report.AppendLine();
        report.AppendLine(
            "| Implementation | Time | Total allocated | Retained | Approx. peak live | Elements | Text nodes | Attributes | Retained / node | Retained / attribute |"
        );
        report.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        Append(standard);
        foreach (var profile in readOnly)
        {
            Append(profile);
        }
        return report.ToString();

        void Append(Measurement value)
        {
            var nodes = value.Counts.Elements + value.Counts.TextNodes;
            report.AppendLine(
                $"| {value.Implementation} | {value.Elapsed.TotalMilliseconds:F1} ms | {Bytes(value.Allocated)} | {Bytes(value.Retained)} | {Bytes(value.PeakLive)} | {value.Counts.Elements:N0} | {value.Counts.TextNodes:N0} | {value.Counts.Attributes:N0} | {Divide(value.Retained, nodes)} | {Divide(value.Retained, value.Counts.Attributes)} |"
            );
        }
    }

    private static string GetCommit()
    {
        try
        {
            var start = new ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(start)!;
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

    private static Measurement Median(Measurement[] values)
    {
        var first = values[0];
        return first with
        {
            Elapsed = TimeSpan.FromTicks(Median(values.Select(item => item.Elapsed.Ticks))),
            Allocated = Median(values.Select(item => item.Allocated)),
            Retained = Median(values.Select(item => item.Retained)),
            PeakLive = Median(values.Select(item => item.PeakLive)),
        };
    }

    private static long Median(IEnumerable<long> values)
    {
        var ordered = values.Order().ToArray();
        return ordered[ordered.Length / 2];
    }

    private static string Divide(long bytes, int count) => count == 0 ? "n/a" : $"{bytes / (double)count:N1} B";

    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private readonly record struct Measurement(
        string Implementation,
        TimeSpan Elapsed,
        long Allocated,
        long Retained,
        long PeakLive,
        Counts Counts
    );

    private record struct Counts(int Elements, int TextNodes, int Attributes)
    {
        public static Counts operator +(Counts left, Counts right) =>
            new(left.Elements + right.Elements, left.TextNodes + right.TextNodes, left.Attributes + right.Attributes);
    }
#endif

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
