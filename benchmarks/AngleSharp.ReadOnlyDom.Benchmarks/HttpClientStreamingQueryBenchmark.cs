#if NET10_0
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming.Utf8Stream;
using AngleSharp.ReadOnlyDom.Streaming.Utf8Stream.Query;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[MemoryDiagnoser]
public class HttpClientStreamingQueryBenchmark
{
    private static readonly QueryPlan<ByteCountState> Plan = CreatePlan();

    private readonly CancellationTokenSource _stop = new();
    private TcpListener _listener = null!;
    private Task _server = null!;
    private HttpClient _client = null!;
    private byte[] _payload = null!;
    private Uri _url = null!;

    [Params(128 * 1024, 2 * 1024 * 1024)]
    public int HtmlBytes { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _payload = Bake(HtmlBytes);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _url = new Uri($"http://127.0.0.1:{port}/fixture");
        _client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        _server = ServeAsync(_stop.Token);

        var materialized = await MaterializeThenFold();
        var streamed = await ResponseStreamFold();
        if (materialized != streamed)
            throw new InvalidOperationException(
                $"HTTP lanes disagree: materialized={materialized}, streamed={streamed}."
            );
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _stop.Cancel();
        _listener.Stop();
        _client.Dispose();
        try
        {
            await _server;
        }
        catch (OperationCanceledException) { }
        _stop.Dispose();
    }

    [Benchmark(Baseline = true)]
    public async Task<int> MaterializeThenFold()
    {
        var utf8 = await _client.GetByteArrayAsync(_url);
        return Plan.Execute(utf8, new ByteCountState()).Bytes;
    }

    [Benchmark]
    public async Task<int> ResponseStreamFold()
    {
        using var response = await _client.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: 4 * 1024, leaveOpen: true));
        try
        {
            return (await Plan.ExecuteAsync(reader, new ByteCountState())).Bytes;
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            await ReadRequestHeadersAsync(stream, cancellationToken);
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {_payload.Length}\r\nConnection: close\r\n\r\n"
            );
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(_payload, cancellationToken);
        }
    }

    private static async ValueTask ReadRequestHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        var matched = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("The HTTP client closed before sending request headers.");
            for (var index = 0; index < read; index++)
            {
                matched = (matched, buffer[index]) switch
                {
                    (0, (byte)'\r') => 1,
                    (1, (byte)'\n') => 2,
                    (2, (byte)'\r') => 3,
                    (3, (byte)'\n') => 4,
                    (_, (byte)'\r') => 1,
                    _ => 0,
                };
                if (matched == 4)
                    return;
            }
        }
    }

    private static byte[] Bake(int targetBytes)
    {
        const string prefix = "<html><body><article>";
        const string row = "<p>abcdefghijklmnopqrstuvwxyz 0123456789 repeated network fixture text.</p>";
        const string suffix = "</article></body></html>";
        var rows = Math.Max(1, (targetBytes - prefix.Length - suffix.Length) / row.Length);
        return Encoding.UTF8.GetBytes(prefix + string.Concat(Enumerable.Repeat(row, rows)) + suffix);
    }

    private static QueryPlan<ByteCountState> CreatePlan() =>
        StreamQuery
            .For<ByteCountState>("html")
            .OnText(static (ref state, text) => state.Bytes += text.Length)
            .Compile();

    private sealed class ByteCountState
    {
        internal int Bytes;
    }
}
#endif
