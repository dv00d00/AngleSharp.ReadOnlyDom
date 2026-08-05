using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming.Query;
using AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;

var options = Options.Parse(args);
var source = await File.ReadAllBytesAsync(options.Input);
var input = RepeatBody(source, options.Copies);
var urlQuery = CreateUrlQuery();
var matchQuery = CreateMatchQuery();
var passThroughQuery = StreamQuery.For<CountState>("zz").Compile();
var rewriteQuery = CreateRewriteQuery();

BenchmarkResult last = default;
for (var index = 0; index < options.Warmup; index++)
    last = await Parse(urlQuery, matchQuery, passThroughQuery, rewriteQuery, input, options);

GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
GC.WaitForPendingFinalizers();
GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
var gen0Before = GC.CollectionCount(0);
var gen1Before = GC.CollectionCount(1);
var gen2Before = GC.CollectionCount(2);
var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
var started = Stopwatch.GetTimestamp();
var deadline = started + (long)(options.Seconds * Stopwatch.Frequency);
long requests = 0;
long checksum = 0;
do
{
    last = await Parse(urlQuery, matchQuery, passThroughQuery, rewriteQuery, input, options);
    checksum = unchecked(checksum + last.Checksum);
    requests++;
} while (Stopwatch.GetTimestamp() < deadline);
var finished = Stopwatch.GetTimestamp();
var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
var gen0Collections = GC.CollectionCount(0) - gen0Before;
var gen1Collections = GC.CollectionCount(1) - gen1Before;
var gen2Collections = GC.CollectionCount(2) - gen2Before;

Console.WriteLine(
    string.Create(
        CultureInfo.InvariantCulture,
        $"RESULT service=AngleSharp workload={options.Workload} mode={options.Mode} copies={options.Copies} requests={requests} elapsed_ms={Stopwatch.GetElapsedTime(started, finished).TotalMilliseconds:F3} cpu_ms={cpu.TotalMilliseconds:F3} allocated_bytes={allocatedBytes} allocated_bytes_per_request={(double)allocatedBytes / requests:F1} gen0={gen0Collections} gen1={gen1Collections} gen2={gen2Collections} checksum={checksum} value_checksum={last.Checksum} urls={last.Urls} bytes={input.Length}"
    )
);

static async ValueTask<BenchmarkResult> Parse(
    QueryPlan<UrlState> urlQuery,
    QueryPlan<CountState> matchQuery,
    QueryPlan<CountState> passThroughQuery,
    QueryPlan<CountState> rewriteQuery,
    byte[] input,
    Options options
)
{
    if (options.Workload == "rewrite")
        return RewriteBuffered(rewriteQuery, input);
    if (options.Workload == "rewrite-sink")
        return RewriteSink(rewriteQuery, input);

    if (options.Workload == "extract")
    {
        var state = options.Mode switch
        {
            "stream" => await urlQuery.ExecuteAsync(
                new ChunkedMemoryPipeReader(input, options.ChunkSize),
                new UrlState()
            ),
            "stream-trusted" => await urlQuery.ExecuteAsync(
                new ChunkedMemoryPipeReader(input, options.ChunkSize),
                new UrlState(),
                inputContract: Utf8InputContract.WellFormedUtf8
            ),
            "push" => PushParse(urlQuery, input, options.ChunkSize),
            "buffer-arbitrary" => urlQuery.Execute(input, new UrlState(), Utf8InputContract.ArbitraryBytes),
            "buffer-trusted" => urlQuery.Execute(input, new UrlState(), Utf8InputContract.WellFormedUtf8),
            _ => throw new ArgumentException($"Unknown mode: {options.Mode}"),
        };
        long checksum = 17;
        foreach (var url in state.Urls)
            foreach (var value in url)
                checksum = unchecked(checksum * 31 + value);
        return new BenchmarkResult(state.Urls.Count, checksum);
    }

    var query = options.Workload == "match" ? matchQuery : passThroughQuery;
    var count = options.Mode switch
    {
        "stream" => await query.ExecuteAsync(
            new ChunkedMemoryPipeReader(input, options.ChunkSize),
            new CountState()
        ),
        "stream-trusted" => await query.ExecuteAsync(
            new ChunkedMemoryPipeReader(input, options.ChunkSize),
            new CountState(),
            inputContract: Utf8InputContract.WellFormedUtf8
        ),
        "push" => PushParse(query, input, options.ChunkSize),
        "buffer-arbitrary" => query.Execute(input, new CountState(), Utf8InputContract.ArbitraryBytes),
        "buffer-trusted" => query.Execute(input, new CountState(), Utf8InputContract.WellFormedUtf8),
        _ => throw new ArgumentException($"Unknown mode: {options.Mode}"),
    };
    return new BenchmarkResult(count.Count, count.Count);
}

// The rewritten document checksum is deterministic per corpus, so it is computed once (during
// warmup) and skipped in the hot loop - a full-output checksum pass would otherwise rival the
// rewrite itself and mask the publish cost the workload exists to measure.
static BenchmarkResult RewriteBuffered(QueryPlan<CountState> plan, byte[] input)
{
    RewriteScratch.Output ??= new ArrayBufferWriter<byte>(input.Length + 4096);
    var output = RewriteScratch.Output;
    output.ResetWrittenCount();
    var state = plan.Rewrite(
        input,
        output,
        new CountState(),
        static (ref CountState state, in Element _, ref StartTagEditor tag) =>
        {
            state.Count++;
            tag.AppendAttribute("data-q"u8, "1"u8);
        },
        Utf8InputContract.WellFormedUtf8
    );
    RewriteScratch.Checksum ??= Fnv(output.WrittenSpan);
    return new BenchmarkResult(state.Count, RewriteScratch.Checksum.Value);
}

static BenchmarkResult RewriteSink(QueryPlan<CountState> plan, byte[] input)
{
    var checksumThisPass = RewriteScratch.Checksum is null;
    var state = plan.Rewrite(
        input,
        new CountState(),
        static (ref CountState state, in Element _, ref StartTagEditor tag) =>
        {
            state.Count++;
            tag.AppendAttribute("data-q"u8, "1"u8);
        },
        checksumThisPass
            ? static (ref CountState _, ReadOnlySpan<byte> segment) =>
                RewriteScratch.Accumulator = Fnv(segment, RewriteScratch.Accumulator)
            : static (ref CountState _, ReadOnlySpan<byte> _) => { },
        Utf8InputContract.WellFormedUtf8
    );
    if (checksumThisPass)
        RewriteScratch.Checksum = RewriteScratch.Accumulator;
    return new BenchmarkResult(state.Count, RewriteScratch.Checksum!.Value);
}

static long Fnv(ReadOnlySpan<byte> value, long checksum = 17)
{
    foreach (var item in value)
        checksum = unchecked(checksum * 31 + item);
    return checksum;
}

static TState PushParse<TState>(QueryPlan<TState> plan, byte[] input, int chunkSize)
    where TState : new()
{
    using var session = plan.CreateSession(new TState());
    for (var offset = 0; offset < input.Length; offset += chunkSize)
    {
        session.Write(input.AsSpan(offset, Math.Min(chunkSize, input.Length - offset)));
    }
    return session.Complete();
}

static QueryPlan<UrlState> CreateUrlQuery()
{
    var list = StreamQuery.For<UrlState>("ul").Class("news-list");
    var card = list.Descendant("li").Attribute("dt-eid", "em_item_article");
    card.Descendant("a")
        .Attribute("href")
        .OnStart(static (ref state, in element) => state.Add(element), "href");
    return list.Compile();
}

static QueryPlan<CountState> CreateMatchQuery()
{
    var list = StreamQuery.For<CountState>("ul").Class("news-list");
    var card = list.Descendant("li").Attribute("dt-eid", "em_item_article");
    card.Descendant("a")
        .Attribute("href")
        .OnStart(static (ref state, in _) => state.Count++);
    return list.Compile();
}

static QueryPlan<CountState> CreateRewriteQuery()
{
    // No OnStart handler: the rewrite handler itself counts, matching the lol-html lane.
    var list = StreamQuery.For<CountState>("ul").Class("news-list");
    var card = list.Descendant("li").Attribute("dt-eid", "em_item_article");
    card.Descendant("a").Attribute("href");
    return list.Compile();
}

static byte[] RepeatBody(byte[] source, int copies)
{
    if (copies == 1)
        return source;

    var text = Encoding.UTF8.GetString(source);
    var bodyOpen = text.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
    var bodyContent = bodyOpen < 0 ? -1 : text.IndexOf('>', bodyOpen) + 1;
    var bodyClose = text.LastIndexOf("</body", StringComparison.OrdinalIgnoreCase);
    if (bodyContent <= 0 || bodyClose < bodyContent)
        throw new InvalidOperationException("Corpus does not contain a complete body element.");

    var body = text.AsSpan(bodyContent, bodyClose - bodyContent);
    var output = new StringBuilder(text.Length + body.Length * (copies - 1));
    output.Append(text.AsSpan(0, bodyContent));
    for (var index = 0; index < copies; index++)
        output.Append(body);
    output.Append(text.AsSpan(bodyClose));
    return Encoding.UTF8.GetBytes(output.ToString());
}

sealed class UrlState
{
    public List<byte[]> Urls { get; } = [];

    public void Add(in Element element)
    {
        if (element.TryGetAttribute("href", out var value))
            Urls.Add(value.ToArray());
    }
}

sealed class CountState
{
    public int Count;
}

static class RewriteScratch
{
    public static ArrayBufferWriter<byte>? Output;
    public static long? Checksum;
    public static long Accumulator = 17;
}

sealed class ChunkedMemoryPipeReader(byte[] source, int chunkSize) : PipeReader
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

readonly record struct BenchmarkResult(int Urls, long Checksum);

sealed record Options(
    string Input,
    double Seconds,
    int Warmup,
    int Copies,
    int ChunkSize,
    string Mode,
    string Workload
)
{
    public static Options Parse(string[] args)
    {
        var values = args.Chunk(2).ToDictionary(pair => pair[0], pair => pair[1]);
        var workload = values.GetValueOrDefault("--workload", "extract");
        if (workload is not ("passthrough" or "match" or "extract" or "rewrite" or "rewrite-sink"))
            throw new ArgumentException($"Unknown workload: {workload}");
        return new Options(
            values["--input"],
            double.Parse(values.GetValueOrDefault("--seconds", "10"), CultureInfo.InvariantCulture),
            int.Parse(values.GetValueOrDefault("--warmup", "120"), CultureInfo.InvariantCulture),
            int.Parse(values.GetValueOrDefault("--copies", "1"), CultureInfo.InvariantCulture),
            int.Parse(values.GetValueOrDefault("--chunk-size", "4096"), CultureInfo.InvariantCulture),
            values.GetValueOrDefault("--mode", "stream"),
            workload
        );
    }
}
