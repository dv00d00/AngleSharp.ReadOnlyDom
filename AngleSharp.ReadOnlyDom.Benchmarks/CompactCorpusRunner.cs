#if NET10_0
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using AngleSharp.ReadOnlyDom.Compact;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

internal static class CompactCorpusRunner
{
    public static int Run(string[] args)
    {
        var tier = GetOption(args, "--tier") ?? "small";
        var iterations = int.TryParse(GetOption(args, "--iterations"), out var value) && value > 0 ? value : 5;
        var output = GetOption(args, "--output");
        var documents = BenchmarkCorpus.Load(tier);
        var measurements = documents.Select(document => Measure(document, iterations)).ToArray();
        var report = Render(tier, iterations, measurements);
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

    private static Measurement Measure(CorpusDocument source, int iterations)
    {
        var parser = CompactParser.CreateParser();
        using (var warmup = parser.ParseCompactDocument(source.Html))
            GC.KeepAlive(warmup.NodeCount);

        ForceCollection();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var nodes = 0;
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            using var document = parser.ParseCompactDocument(source.Html);
            nodes = document.NodeCount;
        }
        stopwatch.Stop();
        var allocated = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / iterations;
        return new Measurement(
            source.Name,
            source.Html.Length,
            nodes,
            stopwatch.Elapsed.TotalMicroseconds / iterations,
            allocated
        );
    }

    private static string Render(string tier, int iterations, IReadOnlyList<Measurement> measurements)
    {
        var report = new StringBuilder();
        report.AppendLine("# Compact corpus parse report").AppendLine();
        report.AppendLine($"- Runtime: `{RuntimeInformation.FrameworkDescription}`");
        report.AppendLine($"- OS: `{RuntimeInformation.OSDescription}`");
        report.AppendLine($"- GC: `{(GCSettings.IsServerGC ? "Server" : "Workstation")}`");
        report.AppendLine($"- Corpus: `{tier}`");
        report.AppendLine($"- Iterations: `{iterations}` per document").AppendLine();
        report.AppendLine("| Document | Input | Nodes | Parse mean | Allocated |");
        report.AppendLine("| --- | ---: | ---: | ---: | ---: |");
        foreach (var measurement in measurements)
            report.AppendLine(
                $"| {measurement.Document} | {measurement.InputChars:N0} chars | {measurement.Nodes:N0} | {measurement.Microseconds:N1} us | {measurement.Allocated / 1024d:N2} KB |"
            );
        return report.ToString();
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        return null;
    }

    private static void ForceCollection()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private sealed record Measurement(string Document, int InputChars, int Nodes, double Microseconds, long Allocated);
}
#endif
