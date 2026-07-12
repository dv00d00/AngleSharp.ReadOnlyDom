using BenchmarkDotNet.Running;

namespace AngleSharp.ReadOnlyDom.Benchmarks
{
    static class Program
    {
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0].Equals("--retained", StringComparison.OrdinalIgnoreCase))
            {
                return RetainedMemoryRunner.Run(args.Skip(1).ToArray());
            }

            if (args.Length > 0 && args[0].Equals("--collection-shapes", StringComparison.OrdinalIgnoreCase))
            {
                return CollectionShapeRunner.Run(args.Skip(1).ToArray());
            }

            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
            return 0;
        }
    }
}
