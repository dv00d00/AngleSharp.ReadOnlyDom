#if NET10_0
using AngleSharp.ReadOnlyDom.Benchmarks.Suites.Utf8;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;

namespace AngleSharp.ReadOnlyDom.Benchmarks.Support;

// Prints the tokenizer's per-state byte distribution for real corpus files, plus their non-ASCII
// byte fraction. This is how optimisation targets get sized: a corpus that spends 95% of its
// bytes in three bulk states with kilobyte mean runs rewards a different change than one that
// spends them across fifteen states at 20 bytes a run.
//
//   dotnet AngleSharp.ReadOnlyDom.Benchmarks.dll --utf8-state-profile <file.html> [more files]
internal static class Utf8StateProfileRunner
{
    public static int Run(string[] args)
    {
        foreach (var path in args)
        {
            var utf8 = File.ReadAllBytes(path);
            var nonAscii = 0L;
            foreach (var value in utf8)
            {
                if (value >= 0x80)
                    nonAscii++;
            }

            var sink = new Utf8TokenizerBaselineBenchmark.FingerprintSink();
            var metrics = new Utf8HtmlTokenizerStateMetrics(Utf8HtmlTokenizer.StateCount);
            var tokenizer = new Utf8HtmlTokenizer(sink, metrics);
            tokenizer.Write(utf8);
            tokenizer.Complete();

            Console.WriteLine(
                $"## {Path.GetFileName(path)} bytes={utf8.Length:N0} nonAscii={100.0 * nonAscii / utf8.Length:F1}%"
            );
            foreach (var metric in tokenizer.GetStateMetrics())
            {
                var mean = metric.Runs == 0 ? 0 : (double)metric.ByteVisits / metric.Runs;
                Console.WriteLine(
                    $"  {metric.State, -28} {metric.ByteVisits, 10:N0} ({100.0 * metric.ByteVisits / utf8.Length, 5:F1}%)  runs={metric.Runs, 7:N0} meanRun={mean, 8:N1}"
                );
            }
        }
        return 0;
    }
}
#endif
