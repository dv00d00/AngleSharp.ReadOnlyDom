#if NET10_0
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

internal static class Utf8TokenizerBaselineRunner
{
    public static int Run(string[] args)
    {
        var output = ReadOption(args, "--output") ?? "utf8-tokenizer-baseline.md";
        var lines = new List<string>
        {
            "# UTF-8 tokenizer baseline diagnostics",
            "",
            $"- Runtime: `{Environment.Version}`",
            $"- Payload per workload: `{Utf8TokenizerBaselineCorpus.PayloadBytes:N0}` bytes",
            "- State byte visits include reconsumed input bytes and can therefore exceed source length.",
            "- Timing, allocation, throughput, and optional hardware counters are in the adjacent BenchmarkDotNet report.",
            "",
        };

        foreach (var workload in Enum.GetValues<Utf8BaselineWorkload>())
        {
            var utf8 = Utf8TokenizerBaselineCorpus.Create(workload);
            var sink = new Utf8TokenizerBaselineBenchmark.FingerprintSink();
            var metrics = new Utf8HtmlTokenizerStateMetrics(Utf8HtmlTokenizer.StateCount);
            var tokenizer = new Utf8HtmlTokenizer(sink, metrics);
            tokenizer.Write(utf8);
            tokenizer.Complete();

            lines.Add($"## {workload}");
            lines.Add("");
            lines.Add($"- Source bytes: `{tokenizer.Counters.BytesConsumed:N0}`");
            lines.Add($"- Maximum buffered token bytes: `{tokenizer.Counters.MaximumBufferedTokenBytes:N0}`");
            lines.Add($"- Reconsumes: `{tokenizer.Counters.Reconsumes:N0}`");
            lines.Add("");
            lines.Add("| State | Byte visits | Runs | Maximum run | Mean run |");
            lines.Add("| --- | ---: | ---: | ---: | ---: |");
            foreach (var metric in tokenizer.GetStateMetrics())
            {
                var mean = metric.Runs == 0 ? 0 : (double)metric.ByteVisits / metric.Runs;
                lines.Add(
                    $"| {metric.State} | {metric.ByteVisits:N0} | {metric.Runs:N0} | "
                        + $"{metric.MaximumRunLength:N0} | {mean:N1} |"
                );
            }
            lines.Add("");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (directory is not null)
            Directory.CreateDirectory(directory);
        File.WriteAllLines(output, lines, Encoding.UTF8);
        Console.WriteLine($"UTF-8 tokenizer baseline diagnostics: {Path.GetFullPath(output)}");
        return 0;
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }
        return null;
    }
}
#endif
