using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming;
using AngleSharp.ReadOnlyDom.Streaming.Query;
using AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;

var options = Options.Parse(args);
RewriteScratch.DumpPath = options.Dump;
var source = await File.ReadAllBytesAsync(options.Input);
var input = RepeatBody(source, options.Copies);
var urlQuery = CreateUrlQuery(options.Query);
var matchQuery = CreateMatchQuery(options.Query);
var passThroughQuery = StreamQuery.For<CountState>("zz").Compile();
var rewriteQuery = CreateRewriteQuery(options.Query);

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
    var limits = options.Unlimited ? HtmlStreamingLimits.Unlimited : null;
    if (options.Workload == "rewrite")
        return RewriteBuffered(rewriteQuery, input, limits);
    if (options.Workload == "rewrite-sink")
        return RewriteSink(rewriteQuery, input, limits);
    if (options.Workload == "rewrite-stream")
        return RewriteStream(rewriteQuery, input, options.ChunkSize, limits);

    if (options.Workload == "extract")
    {
        var state = options.Mode switch
        {
            "stream" => await urlQuery.ExecuteAsync(
                new ChunkedMemoryPipeReader(input, options.ChunkSize),
                new UrlState(),
                limits: limits
            ),
            "stream-trusted" => await urlQuery.ExecuteAsync(
                new ChunkedMemoryPipeReader(input, options.ChunkSize),
                new UrlState(),
                limits: limits,
                inputContract: Utf8InputContract.WellFormedUtf8
            ),
            "push" => PushParse(urlQuery, input, options.ChunkSize, limits),
            "buffer-arbitrary" => urlQuery.Execute(input, new UrlState(), Utf8InputContract.ArbitraryBytes, limits),
            "buffer-trusted" => urlQuery.Execute(input, new UrlState(), Utf8InputContract.WellFormedUtf8, limits),
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
            new CountState(),
            limits: limits
        ),
        "stream-trusted" => await query.ExecuteAsync(
            new ChunkedMemoryPipeReader(input, options.ChunkSize),
            new CountState(),
            limits: limits,
            inputContract: Utf8InputContract.WellFormedUtf8
        ),
        "push" => PushParse(query, input, options.ChunkSize, limits),
        "buffer-arbitrary" => query.Execute(input, new CountState(), Utf8InputContract.ArbitraryBytes, limits),
        "buffer-trusted" => query.Execute(input, new CountState(), Utf8InputContract.WellFormedUtf8, limits),
        _ => throw new ArgumentException($"Unknown mode: {options.Mode}"),
    };
    return new BenchmarkResult(count.Count, count.Count);
}

// The rewritten document checksum is deterministic per corpus, so it is computed once (during
// warmup) and skipped in the hot loop - a full-output checksum pass would otherwise rival the
// rewrite itself and mask the publish cost the workload exists to measure.
static BenchmarkResult RewriteBuffered(QueryPlan<CountState> plan, byte[] input, HtmlStreamingLimits? limits)
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
        Utf8InputContract.WellFormedUtf8,
        limits
    );
    RewriteScratch.Checksum ??= Fnv(output.WrittenSpan);
    return new BenchmarkResult(state.Count, RewriteScratch.Checksum.Value);
}

static BenchmarkResult RewriteSink(QueryPlan<CountState> plan, byte[] input, HtmlStreamingLimits? limits)
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
        Utf8InputContract.WellFormedUtf8,
        limits
    );
    if (checksumThisPass)
        RewriteScratch.Checksum = RewriteScratch.Accumulator;
    return new BenchmarkResult(state.Count, RewriteScratch.Checksum!.Value);
}

// Chunked input, incremental output: the memory-profile-fair counterpart of the lol-html rewrite
// lane, which also streams 4KB chunks through a bounded internal buffer.
static BenchmarkResult RewriteStream(QueryPlan<CountState> plan, byte[] input, int chunkSize, HtmlStreamingLimits? limits)
{
    RewriteScratch.Output ??= new ArrayBufferWriter<byte>(input.Length + 4096);
    var output = RewriteScratch.Output;
    output.ResetWrittenCount();
    using var session = plan.CreateRewriteSession(
        new CountState(),
        output,
        static (ref CountState state, in Element _, ref StartTagEditor tag) =>
        {
            state.Count++;
            tag.AppendAttribute("data-q"u8, "1"u8);
        },
        Utf8InputContract.WellFormedUtf8,
        limits
    );
    for (var offset = 0; offset < input.Length; offset += chunkSize)
        session.Write(input.AsSpan(offset, Math.Min(chunkSize, input.Length - offset)));
    var state = session.Complete();
    if (RewriteScratch.Checksum is null && RewriteScratch.DumpPath is not null)
        File.WriteAllBytes(RewriteScratch.DumpPath, output.WrittenSpan.ToArray());
    RewriteScratch.Checksum ??= Fnv(output.WrittenSpan);
    return new BenchmarkResult(state.Count, RewriteScratch.Checksum.Value);
}

static long Fnv(ReadOnlySpan<byte> value, long checksum = 17)
{
    foreach (var item in value)
        checksum = unchecked(checksum * 31 + item);
    return checksum;
}

static TState PushParse<TState>(QueryPlan<TState> plan, byte[] input, int chunkSize, HtmlStreamingLimits? limits)
    where TState : new()
{
    using var session = plan.CreateSession(new TState(), limits: limits);
    for (var offset = 0; offset < input.Length; offset += chunkSize)
    {
        session.Write(input.AsSpan(offset, Math.Min(chunkSize, input.Length - offset)));
    }
    return session.Complete();
}

// "qq" is the corpus-specific composite selector; "generic" is a[href], which matches on every
// corpus and gives the rewrite lanes real edit density outside qq.html.
static QueryPlan<UrlState> CreateUrlQuery(string query)
{
    if (query == "generic")
    {
        var anchor = StreamQuery.For<UrlState>("a").Attribute("href");
        anchor.OnStart(static (ref state, in element) => state.Add(element), "href");
        return anchor.Compile();
    }
    var list = StreamQuery.For<UrlState>("ul").Class("news-list");
    var card = list.Descendant("li").Attribute("dt-eid", "em_item_article");
    card.Descendant("a")
        .Attribute("href")
        .OnStart(static (ref state, in element) => state.Add(element), "href");
    return list.Compile();
}

static QueryPlan<CountState> CreateMatchQuery(string query)
{
    if (query == "generic")
    {
        var anchor = StreamQuery.For<CountState>("a").Attribute("href");
        anchor.OnStart(static (ref state, in _) => state.Count++);
        return anchor.Compile();
    }
    var list = StreamQuery.For<CountState>("ul").Class("news-list");
    var card = list.Descendant("li").Attribute("dt-eid", "em_item_article");
    card.Descendant("a")
        .Attribute("href")
        .OnStart(static (ref state, in _) => state.Count++);
    return list.Compile();
}

static QueryPlan<CountState> CreateRewriteQuery(string query)
{
    // No OnStart handler: the rewrite handler itself counts, matching the lol-html lane.
    if (query == "generic")
        return StreamQuery.For<CountState>("a").Attribute("href").Compile();
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
    public static string? DumpPath;
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
    string Workload,
    bool Unlimited,
    string Query,
    string? Dump
)
{
    public static Options Parse(string[] args)
    {
        var values = args.Chunk(2).ToDictionary(pair => pair[0], pair => pair[1]);
        var workload = values.GetValueOrDefault("--workload", "extract");
        if (workload is not ("passthrough" or "match" or "extract" or "rewrite" or "rewrite-sink" or "rewrite-stream"))
            throw new ArgumentException($"Unknown workload: {workload}");
        var query = values.GetValueOrDefault("--query", "qq");
        if (query is not ("qq" or "generic"))
            throw new ArgumentException($"Unknown query: {query}");
        return new Options(
            values["--input"],
            double.Parse(values.GetValueOrDefault("--seconds", "10"), CultureInfo.InvariantCulture),
            int.Parse(values.GetValueOrDefault("--warmup", "120"), CultureInfo.InvariantCulture),
            int.Parse(values.GetValueOrDefault("--copies", "1"), CultureInfo.InvariantCulture),
            int.Parse(values.GetValueOrDefault("--chunk-size", "4096"), CultureInfo.InvariantCulture),
            values.GetValueOrDefault("--mode", "stream"),
            workload,
            bool.Parse(values.GetValueOrDefault("--unlimited", "false")),
            query,
            values.GetValueOrDefault("--dump")
        );
    }
}
