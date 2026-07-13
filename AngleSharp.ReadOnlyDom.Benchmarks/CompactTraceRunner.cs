#if NET10_0
using System.Diagnostics;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

internal static class CompactTraceRunner
{
    public static int Run(string[] args)
    {
        var workload = GetOption(args, "--workload") ?? "readonly-selected";
        var warmupIterations = GetPositiveInt(args, "--warmup", 10_000);
        var iterations = GetPositiveInt(args, "--iterations", 100_000);
        var benchmarks = new CompactQueryWorkloadBenchmark();
        benchmarks.ValidateWorkloads();
        Func<int> action = workload.ToLowerInvariant() switch
        {
            "readonly-selected" => benchmarks.ReadOnlySelectedSubtreeQuery,
            "compact-selected" => benchmarks.FrozenSelectedSubtreeQuery,
            "packed-selected" => benchmarks.PackedSelectedSubtreeQuery,
            "readonly-text" => benchmarks.ReadOnlyAttributeFreeTextQuery,
            "compact-text" => benchmarks.FrozenAttributeFreeTextQuery,
            "packed-text" => benchmarks.PackedAttributeFreeTextQuery,
            _ => throw new ArgumentException($"Unknown compact trace workload '{workload}'.", nameof(args)),
        };

        var checksum = RunLoop(action, warmupIterations);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var stopwatch = Stopwatch.StartNew();
        checksum += RunLoop(action, iterations);
        stopwatch.Stop();

        Console.WriteLine(
            $"workload={workload} iterations={iterations} elapsed={stopwatch.Elapsed.TotalSeconds:F3}s "
                + $"mean={stopwatch.Elapsed.TotalNanoseconds / iterations:F1}ns checksum={checksum}"
        );
        return 0;
    }

    private static long RunLoop(Func<int> action, int iterations)
    {
        long checksum = 0;
        for (var iteration = 0; iteration < iterations; iteration++)
            checksum += action();
        return checksum;
    }

    private static int GetPositiveInt(string[] args, string name, int fallback)
    {
        var value = GetOption(args, name);
        if (value is null)
            return fallback;
        return int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentOutOfRangeException(name, "Iteration counts must be positive integers.");
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        return null;
    }
}
#endif
