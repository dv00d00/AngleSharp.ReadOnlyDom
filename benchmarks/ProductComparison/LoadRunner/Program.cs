using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

var options = Options.Parse(args);
var qq = await File.ReadAllBytesAsync(options.CorpusPath);
var corpora = new[]
{
    new Corpus("qq", qq, 16),
    new Corpus("qq-x4", RepeatBody(qq, 4), 64),
};

using var angle = ServerProcess.Start("AngleSharp NativeAOT", options.AngleServer, 5081);
using var lol = ServerProcess.Start("lol-html Rust", options.LolServer, 5082);
using var angleJit = options.AngleJitServer is null
    ? null
    : ServerProcess.Start("AngleSharp JIT", options.AngleJitServer, 5083);
await angle.WaitUntilReady();
await lol.WaitUntilReady();
if (angleJit is not null)
    await angleJit.WaitUntilReady();

using var angleClient = CreateClient(6);
using var lolClient = CreateClient(6);
using var angleJitClient = angleJit is null ? null : CreateClient(6);
var services = new List<Service>
{
    new("AngleSharp NativeAOT", angle, angleClient, angle.EndpointUri(options.Endpoint)),
    new("lol-html Rust", lol, lolClient, lol.EndpointUri(options.Endpoint)),
};
if (angleJit is not null)
    services.Add(new Service("AngleSharp JIT", angleJit, angleJitClient!, angleJit.EndpointUri(options.Endpoint)));

var results = new List<LaneRun>();
string? report = null;
try
{
    foreach (var corpus in corpora)
    {
        var expected = await SendOnce(services[0].Client, services[0].Target, corpus.Bytes);
        foreach (var service in services.Skip(1))
        {
            var actual = await SendOnce(service.Client, service.Target, corpus.Bytes);
            if (!expected.AsSpan().SequenceEqual(actual))
                throw new InvalidOperationException($"{service.Name} returned different output for {corpus.Name}.");
        }
        var observed = options.Endpoint == "rewrite"
            ? CountOccurrences(expected, "data-q=\"1\""u8)
            : CountLines(expected);
        if (observed != corpus.ExpectedUrls)
            throw new InvalidOperationException($"Unexpected match count for {corpus.Name}: {observed}.");

        foreach (var concurrency in options.Concurrency)
        {
            foreach (var service in services)
                await WarmUp(service, corpus, concurrency, options.WarmupRequests);

            for (var round = 1; round <= options.Rounds; round++)
            {
                var order = round % 2 == 0 ? Enumerable.Reverse(services) : services;
                foreach (var service in order)
                {
                    var result = await Measure(service, corpus, concurrency, options.Duration, round, expected);
                    results.Add(result);
                    Console.WriteLine(
                        $"{corpus.Name,-6} c={concurrency} round={round} {service.Name,-20} "
                            + $"{result.Requests / result.Elapsed.TotalSeconds,9:N1} req/s, "
                            + $"p50 {Percentile(result.Latencies, 0.50),7:N1} us, "
                            + $"p95 {Percentile(result.Latencies, 0.95),7:N1} us"
                    );
                }
            }
        }
    }
    report = BuildReport(options, results, angle, lol, angleJit);
}
finally
{
    angle.Stop();
    lol.Stop();
    angleJit?.Stop();
}

if (report is null)
    throw new InvalidOperationException("The product comparison did not produce a report.");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
await File.WriteAllTextAsync(options.OutputPath, report);
Console.WriteLine();
Console.WriteLine(report);
Console.WriteLine($"Report: {Path.GetFullPath(options.OutputPath)}");

static HttpClient CreateClient(int maximumConnections) =>
    new(
        new SocketsHttpHandler
        {
            MaxConnectionsPerServer = maximumConnections,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
        }
    )
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

static async Task WarmUp(Service service, Corpus corpus, int concurrency, int requests)
{
    var perWorker = Math.Max(1, requests / concurrency);
    await Task.WhenAll(
        Enumerable.Range(0, concurrency).Select(async worker =>
        {
            _ = worker;
            for (var index = 0; index < perWorker; index++)
                _ = await SendOnce(service.Client, service.Target, corpus.Bytes);
        })
    );
}

static async Task<LaneRun> Measure(
    Service service,
    Corpus corpus,
    int concurrency,
    TimeSpan duration,
    int round,
    byte[] expected
)
{
    service.Process.Process.Refresh();
    var cpuBefore = service.Process.Process.TotalProcessorTime;
    var latencies = new ConcurrentBag<double>();
    var deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
    var started = Stopwatch.GetTimestamp();
    var workers = Enumerable.Range(0, concurrency).Select(Worker).ToArray();
    var counts = await Task.WhenAll(workers);
    var finished = Stopwatch.GetTimestamp();
    service.Process.Process.Refresh();
    var cpu = service.Process.Process.TotalProcessorTime - cpuBefore;
    return new LaneRun(
        service.Name,
        corpus.Name,
        concurrency,
        round,
        counts.Sum(),
        Stopwatch.GetElapsedTime(started, finished),
        cpu,
        latencies.ToArray()
    );

    async Task<long> Worker(int worker)
    {
        _ = worker;
        long count = 0;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var requestStart = Stopwatch.GetTimestamp();
            var response = await SendOnce(service.Client, service.Target, corpus.Bytes);
            var requestEnd = Stopwatch.GetTimestamp();
            if (!expected.AsSpan().SequenceEqual(response))
                throw new InvalidOperationException($"{service.Name} returned inconsistent output.");
            latencies.Add(Stopwatch.GetElapsedTime(requestStart, requestEnd).TotalMicroseconds);
            count++;
        }
        return count;
    }
}

static async Task<byte[]> SendOnce(HttpClient client, Uri endpoint, byte[] payload)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
    {
        Version = HttpVersion.Version11,
        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        Content = new ChunkedContent(payload),
    };
    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsByteArrayAsync();
}

static string BuildReport(
    Options options,
    List<LaneRun> runs,
    ServerProcess angle,
    ServerProcess lol,
    ServerProcess? angleJit
)
{
    var output = new StringBuilder();
    output.AppendLine("# Native HTTP product comparison");
    output.AppendLine();
    output.AppendLine($"- Timestamp: `{DateTimeOffset.Now:O}`");
    output.AppendLine($"- Endpoint: `/{options.Endpoint}`");
    output.AppendLine("- Input: HTTP/1.1 keep-alive, chunked request body written in 4 KiB chunks");
    output.AppendLine($"- Measurement: {options.Rounds} alternating rounds x {options.Duration.TotalSeconds:N0} seconds");
    output.AppendLine($"- Warmup: {options.WarmupRequests} requests per lane");
    output.AppendLine("- Servers: Rust release binary and .NET NativeAOT/Kestrel binary");
    if (angle.PeakWorkingSet64 > 0 && lol.PeakWorkingSet64 > 0)
    {
        output.AppendLine($"- AngleSharp NativeAOT peak working set: {angle.PeakWorkingSet64 / 1024.0 / 1024.0:N1} MiB");
        output.AppendLine($"- lol-html peak working set: {lol.PeakWorkingSet64 / 1024.0 / 1024.0:N1} MiB");
        if (angleJit is not null)
            output.AppendLine($"- AngleSharp JIT peak working set: {angleJit.PeakWorkingSet64 / 1024.0 / 1024.0:N1} MiB");
    }
    output.AppendLine();
    output.AppendLine("| Corpus | Concurrency | Service | Requests | Req/s | p50 | p95 | p99 | CPU ms/request |");
    output.AppendLine("| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |");
    foreach (var group in runs.GroupBy(run => new { run.Corpus, run.Concurrency, run.Service }))
    {
        var requests = group.Sum(run => run.Requests);
        var elapsed = group.Aggregate(TimeSpan.Zero, (sum, run) => sum + run.Elapsed);
        var cpu = group.Aggregate(TimeSpan.Zero, (sum, run) => sum + run.CpuTime);
        var latencies = group.SelectMany(run => run.Latencies).ToArray();
        output.AppendLine(
            $"| {group.Key.Corpus} | {group.Key.Concurrency} | {group.Key.Service} | {requests:N0} | "
                + $"{requests / elapsed.TotalSeconds:N1} | {Percentile(latencies, 0.50):N1} μs | "
                + $"{Percentile(latencies, 0.95):N1} μs | {Percentile(latencies, 0.99):N1} μs | "
                + $"{cpu.TotalMilliseconds / requests:N3} |"
        );
    }
    return output.ToString();
}

static double Percentile(double[] values, double percentile)
{
    Array.Sort(values);
    return values[Math.Clamp((int)Math.Ceiling(values.Length * percentile) - 1, 0, values.Length - 1)];
}

static int CountLines(byte[] value) => value.Count(item => item == (byte)'\n');

static int CountOccurrences(byte[] value, ReadOnlySpan<byte> needle)
{
    var count = 0;
    var span = value.AsSpan();
    for (var index = span.IndexOf(needle); index >= 0; index = span.IndexOf(needle))
    {
        count++;
        span = span[(index + needle.Length)..];
    }
    return count;
}

static byte[] RepeatBody(byte[] utf8, int copies)
{
    var source = Encoding.UTF8.GetString(utf8);
    var bodyOpen = source.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
    var bodyContent = bodyOpen < 0 ? -1 : source.IndexOf('>', bodyOpen) + 1;
    var bodyClose = source.LastIndexOf("</body", StringComparison.OrdinalIgnoreCase);
    if (bodyContent <= 0 || bodyClose < bodyContent)
        throw new InvalidOperationException("Corpus does not contain a complete body element.");
    var body = source.AsSpan(bodyContent, bodyClose - bodyContent);
    var output = new StringBuilder(source.Length + body.Length * (copies - 1));
    output.Append(source.AsSpan(0, bodyContent));
    for (var copy = 0; copy < copies; copy++)
        output.Append(body);
    output.Append(source.AsSpan(bodyClose));
    return Encoding.UTF8.GetBytes(output.ToString());
}

sealed class ChunkedContent(byte[] payload) : HttpContent
{
    private const int ChunkSize = 4 * 1024;

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken
    )
    {
        _ = context;
        for (var offset = 0; offset < payload.Length; offset += ChunkSize)
        {
            var length = Math.Min(ChunkSize, payload.Length - offset);
            await stream.WriteAsync(payload.AsMemory(offset, length), cancellationToken);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

sealed class ServerProcess : IDisposable
{
    private readonly int _port;

    private ServerProcess(string name, Process process, int port)
    {
        Name = name;
        Process = process;
        _port = port;
        HealthUri = new Uri($"http://127.0.0.1:{port}/health");
    }

    public string Name { get; }
    public Process Process { get; }
    public Uri HealthUri { get; }

    public Uri EndpointUri(string endpoint) => new($"http://127.0.0.1:{_port}/{endpoint}");
    public long PeakWorkingSet64
    {
        get
        {
            Process.Refresh();
            return Process.PeakWorkingSet64;
        }
    }

    public static ServerProcess Start(string name, string executable, int port)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!,
        };
        info.Environment["BENCHMARK_PORT"] = port.ToString(CultureInfo.InvariantCulture);
        var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {name}.");
        return new ServerProcess(name, process, port);
    }

    public async Task WaitUntilReady()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(250) };
        for (var attempt = 0; attempt < 120; attempt++)
        {
            if (Process.HasExited)
                throw new InvalidOperationException($"{Name} exited with code {Process.ExitCode}.");
            try
            {
                using var response = await client.GetAsync(HealthUri);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException($"{Name} did not become ready.");
    }

    public void Stop()
    {
        if (Process.HasExited)
            return;
        Process.Kill(entireProcessTree: true);
        Process.WaitForExit();
    }

    public void Dispose()
    {
        Stop();
        Process.Dispose();
    }
}

sealed record Service(string Name, ServerProcess Process, HttpClient Client, Uri Target);
sealed record Corpus(string Name, byte[] Bytes, int ExpectedUrls);
sealed record LaneRun(
    string Service,
    string Corpus,
    int Concurrency,
    int Round,
    long Requests,
    TimeSpan Elapsed,
    TimeSpan CpuTime,
    double[] Latencies
);

sealed record Options(
    string AngleServer,
    string LolServer,
    string? AngleJitServer,
    string CorpusPath,
    string OutputPath,
    string Endpoint,
    int Rounds,
    TimeSpan Duration,
    int WarmupRequests,
    int[] Concurrency
)
{
    public static Options Parse(string[] args)
    {
        var values = args
            .Chunk(2)
            .ToDictionary(pair => pair[0], pair => pair.Length == 2 ? pair[1] : string.Empty);
        var endpoint = values.GetValueOrDefault("--endpoint", "extract");
        if (endpoint is not ("extract" or "rewrite"))
            throw new ArgumentException($"Unknown endpoint: {endpoint}");
        return new Options(
            Required("--angle"),
            Required("--lol"),
            values.GetValueOrDefault("--angle-jit"),
            Required("--corpus"),
            values.GetValueOrDefault("--output", "artifacts/benchmarks/product-comparison.md"),
            endpoint,
            int.Parse(values.GetValueOrDefault("--rounds", "3"), CultureInfo.InvariantCulture),
            TimeSpan.FromSeconds(
                double.Parse(values.GetValueOrDefault("--seconds", "10"), CultureInfo.InvariantCulture)
            ),
            int.Parse(values.GetValueOrDefault("--warmup", "60"), CultureInfo.InvariantCulture),
            values
                .GetValueOrDefault("--concurrency", "1,6")
                .Split(',')
                .Select(value => int.Parse(value, CultureInfo.InvariantCulture))
                .ToArray()
        );

        string Required(string name) =>
            values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ArgumentException($"Missing required option {name}.");
    }
}
