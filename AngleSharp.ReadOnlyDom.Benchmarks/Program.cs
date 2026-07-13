using System.Runtime;
using BenchmarkDotNet.Configs;
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

            if (args.Length > 0 && args[0].Equals("--collection-shapes", StringComparison.OrdinalIgnoreCase))
            {
                return CollectionShapeRunner.Run(args.Skip(1).ToArray());
            }

#if NET10_0
            if (args.Length > 0 && args[0].Equals("--query-workloads", StringComparison.OrdinalIgnoreCase))
            {
                return QueryWorkloadRunner.Run(args.Skip(1).ToArray());
            }

            if (args.Length > 0 && args[0].Equals("--compact-trace", StringComparison.OrdinalIgnoreCase))
            {
                return CompactTraceRunner.Run(args.Skip(1).ToArray());
            }
#endif

            var config = ManualConfig.Create(DefaultConfig.Instance).AddJob(Job.Default.WithGcServer(true));
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
            return 0;
        }
    }
}
