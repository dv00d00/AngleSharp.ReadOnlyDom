using System.Runtime;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

namespace AngleSharp.ReadOnlyDom.Benchmarks
{
    static class Program
    {
        static int Main(string[] args)
        {
            if (!GCSettings.IsServerGC)
                throw new InvalidOperationException("Benchmarks must run with Server GC enabled.");

            if (args.Length > 0 && args[0].Equals("--retained", StringComparison.OrdinalIgnoreCase))
            {
                return RetainedMemoryRunner.Run(args.Skip(1).ToArray());
            }

#if NET10_0
            if (args.Length > 0 && args[0].Equals("--utf8-token-smoke", StringComparison.OrdinalIgnoreCase))
            {
                return Utf8TokenSmoke.Run();
            }

            if (args.Length > 0 && args[0].Equals("--utf8-tokenizer-baseline", StringComparison.OrdinalIgnoreCase))
            {
                return Utf8TokenizerBaselineRunner.Run(args.Skip(1).ToArray());
            }

            if (args.Length > 0 && args[0].Equals("--utf8-dom-check", StringComparison.OrdinalIgnoreCase))
            {
                new Utf8DomProjectionBenchmark().Setup();
                return 0;
            }

            if (args.Length > 0 && args[0].Equals("--qq-scraper-check", StringComparison.OrdinalIgnoreCase))
            {
                var benchmark = new QqArticleScraperBenchmark();
                if (args.Length > 1)
                    benchmark.File = args[1];
                benchmark.Setup();
                return 0;
            }

            if (args.Length > 0 && args[0].Equals("--query-workloads", StringComparison.OrdinalIgnoreCase))
            {
                return QueryWorkloadRunner.Run(args.Skip(1).ToArray());
            }
#endif

            var job =
                Environment.GetEnvironmentVariable("AS_BENCH_LONG") == "1"
                    ? Job.Default.WithId("Sustained").WithLaunchCount(1).WithWarmupCount(10).WithIterationCount(10)
                    : Job.Default.WithLaunchCount(1);
            var config = ManualConfig
                .Create(DefaultConfig.Instance)
                .AddJob(job.WithGcServer(true))
                .AddColumn(StatisticColumn.OperationsPerSecond);
            if (Environment.GetEnvironmentVariable("AS_BENCH_HARDWARE_COUNTERS") == "1")
                config.AddHardwareCounters(HardwareCounter.TotalCycles);
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
            return 0;
        }
    }
}
