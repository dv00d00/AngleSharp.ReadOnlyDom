#if NET10_0
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using AngleSharp.ReadOnlyDom.Streaming;
using AngleSharp.ReadOnlyDom.Streaming.Query;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;

namespace AngleSharp.ReadOnlyDom.Benchmarks.Support;

// Attributes the stream-mode per-chunk tax by peeling the pipeline apart one layer at a time.
// Every variant tokenizes the same document through the same compiled query; each adds one layer:
//
//   buffer          plan.Execute over the whole span (trusted)  - no chunking at all
//   chunk-direct    tokenizer.Write per chunk (trusted)         - adds tokenizer re-entry only
//   chunk-input     tokenizer input per chunk (arbitrary)       - adds the normalizer wrapper
//   pipe-async      plan.ExecuteAsync over a chunked PipeReader - adds the async pipe loop
//
// Variants are interleaved per round so clock drift lands on all of them.
//
//   dotnet AngleSharp.ReadOnlyDom.Benchmarks.dll --stream-tax-profile <file> [chunkSize] [seconds] [variant]
//
// With a variant name the process warms and measures ONLY that variant, reproducing the
// one-hot-path-per-process JIT/PGO conditions of the comparison console; interleave processes
// from a script for a trustworthy A/B.
internal static class StreamTaxProfileRunner
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("usage: --stream-tax-profile <file> [chunkSize] [seconds] [variant]");
            return 2;
        }

        var input = File.ReadAllBytes(args[0]);
        var chunkSize = args.Length > 1 ? int.Parse(args[1]) : 4096;
        var seconds = args.Length > 2 ? double.Parse(args[2]) : 1.5;
        var only = args.Length > 3 ? args[3] : null;
        var plan = StreamQuery.For<CountState>("zz").Compile();

        var variants = new (string Name, Func<long> Parse)[]
        {
            ("buffer", () => ParseBuffer(plan, input)),
            ("chunk-direct", () => ParseChunkDirect(plan, input, chunkSize)),
            ("chunk-input", () => ParseChunkInput(plan, input, chunkSize)),
            ("pipe-async", () => ParsePipeAsync(plan, input, chunkSize)),
            ("pipe-tryread", () => ParsePipeTryRead(plan, input, chunkSize)),
            ("pipe-await-flat", () => ParsePipeAwaitFlat(plan, input, chunkSize)),
            ("pipe-noop", () => ParsePipeNoop(input, chunkSize)),
            ("chunk-input-timed", () => ParseChunkInputTimed(plan, input, chunkSize)),
            ("pipe-tryread-timed", () => ParsePipeTryReadTimed(plan, input, chunkSize)),
        };

        if (only is not null)
        {
            var (name, parse) = Array.Find(variants, candidate => candidate.Name == only);
            if (parse is null)
            {
                Console.WriteLine($"unknown variant: {only}");
                return 2;
            }
            for (var index = 0; index < 150; index++)
                parse();
            DrainWriteAttribution();
            var soloStarted = Stopwatch.GetTimestamp();
            var soloDeadline = soloStarted + (long)(seconds * Stopwatch.Frequency);
            long soloCount = 0;
            do
            {
                parse();
                soloCount++;
            } while (Stopwatch.GetTimestamp() < soloDeadline);
            var soloSeconds = Stopwatch.GetElapsedTime(soloStarted).TotalSeconds;
            var (writeNs, writeCalls) = DrainWriteAttribution();
            Console.WriteLine(
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"RESULT variant={name} requests={soloCount} elapsed_ms={soloSeconds * 1000:F3} mb_per_s={input.Length * soloCount / soloSeconds / (1024 * 1024):F1} write_ns_per_doc={(writeCalls > 0 ? writeNs / soloCount : 0):F0} write_calls_per_doc={(writeCalls > 0 ? writeCalls / soloCount : 0):F1}"
                )
            );
            return 0;
        }

        foreach (var (_, parse) in variants)
        {
            for (var index = 0; index < 40; index++)
                parse();
        }

        var totals = new double[variants.Length];
        var iterations = new long[variants.Length];
        const int Rounds = 5;
        for (var round = 0; round < Rounds; round++)
        {
            for (var variant = 0; variant < variants.Length; variant++)
            {
                var deadline = Stopwatch.GetTimestamp() + (long)(seconds / Rounds * Stopwatch.Frequency);
                var started = Stopwatch.GetTimestamp();
                long count = 0;
                do
                {
                    variants[variant].Parse();
                    count++;
                } while (Stopwatch.GetTimestamp() < deadline);
                totals[variant] += Stopwatch.GetElapsedTime(started).TotalSeconds;
                iterations[variant] += count;
            }
        }

        var chunks = (input.Length + chunkSize - 1) / chunkSize;
        var baseline = totals[0] / iterations[0];
        Console.WriteLine(
            $"file={Path.GetFileName(args[0])} bytes={input.Length} chunk={chunkSize} chunks/doc={chunks}"
        );
        for (var variant = 0; variant < variants.Length; variant++)
        {
            var perDocument = totals[variant] / iterations[variant];
            var mbPerSecond = input.Length / perDocument / (1024 * 1024);
            var taxPerChunk = (perDocument - baseline) / chunks * 1e9;
            Console.WriteLine(
                $"{variants[variant].Name, -12} {mbPerSecond, 9:F1} MB/s  {perDocument * 1e6, 8:F2} us/doc  vs buffer {taxPerChunk, 7:F1} ns/chunk"
            );
        }
        return 0;
    }

    private static long ParseBuffer(QueryPlan<CountState> plan, byte[] input) =>
        plan.Execute(input, new CountState(), Utf8InputContract.WellFormedUtf8).Count;

    private static long ParseChunkDirect(QueryPlan<CountState> plan, byte[] input, int chunkSize)
    {
        var state = new CountState();
        using var execution = plan.CreateExecution(state, HtmlStreamingLimits.Default);
        var tokenizer = new Utf8HtmlTokenizer(execution, HtmlStreamingLimits.Default);
        for (var offset = 0; offset < input.Length; offset += chunkSize)
        {
            tokenizer.Write(input.AsSpan(offset, Math.Min(chunkSize, input.Length - offset)));
        }
        tokenizer.Complete();
        return state.Count;
    }

    private static long ParseChunkInput(QueryPlan<CountState> plan, byte[] input, int chunkSize)
    {
        var state = new CountState();
        using var execution = plan.CreateExecution(state, HtmlStreamingLimits.Default);
        var tokenizer = new Utf8HtmlTokenizer(execution, HtmlStreamingLimits.Default);
        var tokenizerInput = new Utf8HtmlTokenizerInput(tokenizer, Utf8InputContract.ArbitraryBytes);
        for (var offset = 0; offset < input.Length; offset += chunkSize)
        {
            tokenizerInput.Write(input.AsSpan(offset, Math.Min(chunkSize, input.Length - offset)));
        }
        tokenizerInput.Complete();
        return state.Count;
    }

    private static long ParsePipeAsync(QueryPlan<CountState> plan, byte[] input, int chunkSize)
    {
        var task = plan.ExecuteAsync(new ChunkedPipeReader(input, chunkSize), new CountState());
        return (task.IsCompleted ? task.Result : task.AsTask().GetAwaiter().GetResult()).Count;
    }

    // The TokenizeAsync loop shape minus the await: TryRead first, so a synchronously available
    // chunk never touches the ValueTask machinery. Isolates await cost from sequence handling.
    private static long ParsePipeTryRead(QueryPlan<CountState> plan, byte[] input, int chunkSize)
    {
        var state = new CountState();
        using var execution = plan.CreateExecution(state, HtmlStreamingLimits.Default);
        var tokenizer = new Utf8HtmlTokenizer(execution, HtmlStreamingLimits.Default);
        var tokenizerInput = new Utf8HtmlTokenizerInput(tokenizer, Utf8InputContract.ArbitraryBytes);
        var reader = new ChunkedPipeReader(input, chunkSize);
        while (true)
        {
            if (!reader.TryRead(out var result))
            {
                var pending = reader.ReadAsync();
                result = pending.IsCompleted ? pending.Result : pending.AsTask().GetAwaiter().GetResult();
            }
            var buffer = result.Buffer;
            try
            {
                if (buffer.IsSingleSegment)
                {
                    tokenizerInput.Write(buffer.FirstSpan);
                }
                else
                {
                    foreach (var segment in buffer)
                    {
                        tokenizerInput.Write(segment);
                    }
                }
            }
            finally
            {
                reader.AdvanceTo(buffer.End);
            }
            if (result.IsCompleted)
            {
                break;
            }
        }
        tokenizerInput.Complete();
        return state.Count;
    }

    // Attribution: total ticks spent strictly inside Write, accumulated across calls, printed as
    // write_ns_per_doc when the variant finishes. Identical instrumentation on both shapes, so
    // the Stopwatch overhead cancels in the comparison.
    private static long WriteTicks;
    private static long WriteCalls;

    internal static (double WriteNs, double Calls) DrainWriteAttribution()
    {
        var ticks = (double)WriteTicks / Stopwatch.Frequency * 1e9;
        var calls = (double)WriteCalls;
        WriteTicks = 0;
        WriteCalls = 0;
        return (ticks, calls);
    }

    private static long ParseChunkInputTimed(QueryPlan<CountState> plan, byte[] input, int chunkSize)
    {
        var state = new CountState();
        using var execution = plan.CreateExecution(state, HtmlStreamingLimits.Default);
        var tokenizer = new Utf8HtmlTokenizer(execution, HtmlStreamingLimits.Default);
        var tokenizerInput = new Utf8HtmlTokenizerInput(tokenizer, Utf8InputContract.ArbitraryBytes);
        for (var offset = 0; offset < input.Length; offset += chunkSize)
        {
            var started = Stopwatch.GetTimestamp();
            tokenizerInput.Write(input.AsSpan(offset, Math.Min(chunkSize, input.Length - offset)));
            WriteTicks += Stopwatch.GetTimestamp() - started;
            WriteCalls++;
        }
        tokenizerInput.Complete();
        return state.Count;
    }

    private static long ParsePipeTryReadTimed(QueryPlan<CountState> plan, byte[] input, int chunkSize)
    {
        var state = new CountState();
        using var execution = plan.CreateExecution(state, HtmlStreamingLimits.Default);
        var tokenizer = new Utf8HtmlTokenizer(execution, HtmlStreamingLimits.Default);
        var tokenizerInput = new Utf8HtmlTokenizerInput(tokenizer, Utf8InputContract.ArbitraryBytes);
        var reader = new ChunkedPipeReader(input, chunkSize);
        while (true)
        {
            if (!reader.TryRead(out var result))
            {
                break;
            }
            var buffer = result.Buffer;
            try
            {
                var span = buffer.FirstSpan;
                var started = Stopwatch.GetTimestamp();
                tokenizerInput.Write(span);
                WriteTicks += Stopwatch.GetTimestamp() - started;
                WriteCalls++;
            }
            finally
            {
                reader.AdvanceTo(buffer.End);
            }
            if (result.IsCompleted)
            {
                break;
            }
        }
        tokenizerInput.Complete();
        return state.Count;
    }

    // The pipe loop with no parsing at all: measures the pure per-chunk bookkeeping cost of
    // TryRead + ReadResult + AdvanceTo so it can be compared against the parse-bearing variants.
    private static long ParsePipeNoop(byte[] input, int chunkSize)
    {
        var reader = new ChunkedPipeReader(input, chunkSize);
        long total = 0;
        while (true)
        {
            if (!reader.TryRead(out var result))
            {
                break;
            }
            var buffer = result.Buffer;
            try
            {
                if (buffer.IsSingleSegment)
                {
                    total += buffer.FirstSpan.Length;
                }
                else
                {
                    foreach (var segment in buffer)
                    {
                        total += segment.Length;
                    }
                }
            }
            finally
            {
                reader.AdvanceTo(buffer.End);
            }
            if (result.IsCompleted)
            {
                break;
            }
        }
        return total;
    }

    // The exact TokenizeAsync loop (await per chunk) but with the single-segment fast path,
    // isolating ReadOnlySequence enumeration cost from the async machinery.
    private static long ParsePipeAwaitFlat(QueryPlan<CountState> plan, byte[] input, int chunkSize)
    {
        var task = ParsePipeAwaitFlatAsync(plan, input, chunkSize);
        return task.IsCompleted ? task.Result : task.AsTask().GetAwaiter().GetResult();
    }

    private static async ValueTask<long> ParsePipeAwaitFlatAsync(
        QueryPlan<CountState> plan,
        byte[] input,
        int chunkSize
    )
    {
        var state = new CountState();
        using var execution = plan.CreateExecution(state, HtmlStreamingLimits.Default);
        var tokenizer = new Utf8HtmlTokenizer(execution, HtmlStreamingLimits.Default);
        var tokenizerInput = new Utf8HtmlTokenizerInput(tokenizer, Utf8InputContract.ArbitraryBytes);
        var reader = new ChunkedPipeReader(input, chunkSize);
        while (true)
        {
            var result = await reader.ReadAsync().ConfigureAwait(false);
            var buffer = result.Buffer;
            try
            {
                if (buffer.IsSingleSegment)
                {
                    tokenizerInput.Write(buffer.FirstSpan);
                }
                else
                {
                    foreach (var segment in buffer)
                    {
                        tokenizerInput.Write(segment);
                    }
                }
            }
            finally
            {
                reader.AdvanceTo(buffer.End);
            }
            if (result.IsCompleted)
            {
                break;
            }
        }
        tokenizerInput.Complete();
        return state.Count;
    }

    private sealed class CountState
    {
        public int Count { get; set; }
    }

    private sealed class ChunkedPipeReader(byte[] source, int chunkSize) : PipeReader
    {
        private int _position;
        private ReadOnlySequence<byte> _current;

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read());
        }

        public override bool TryRead(out ReadResult result)
        {
            result = Read();
            return true;
        }

        private ReadResult Read()
        {
            var length = Math.Min(chunkSize, source.Length - _position);
            _current = new ReadOnlySequence<byte>(source.AsMemory(_position, length));
            return new ReadResult(_current, isCanceled: false, isCompleted: _position + length == source.Length);
        }

        public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            _ = examined;
            _position += checked((int)_current.Slice(0, consumed).Length);
        }

        public override void CancelPendingRead() { }

        public override void Complete(Exception? exception = null) { }
    }
}
#endif
