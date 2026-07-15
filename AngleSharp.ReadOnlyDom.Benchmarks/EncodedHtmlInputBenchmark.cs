#if NET10_0
using System.IO.Pipelines;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[MemoryDiagnoser]
public class EncodedHtmlInputBenchmark
{
    private byte[] _utf8 = null!;
    private byte[] _windows1252 = null!;
    private byte[] _utf16 = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string row = "<article data-id='42'><h2>café — headline</h2><p>ordinary body text</p></article>";
        var html = "<meta charset=windows-1252>" + string.Concat(Enumerable.Repeat(row, 12_000));
        _utf8 = Encoding.UTF8.GetBytes(html);
        _windows1252 = Encoding.GetEncoding(1252).GetBytes(html);
        _utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(html)).ToArray();

        var direct = await RunUtf8(_utf8);
        if (await RunEncoded(_windows1252, HtmlInputEncoding.Known(Encoding.GetEncoding(1252))) != direct)
            throw new InvalidOperationException("Windows-1252 transcoding changed the token stream.");
        if (await RunEncoded(_windows1252, HtmlInputEncoding.Auto()) != direct)
            throw new InvalidOperationException("Meta encoding detection changed the token stream.");
        if (await RunEncoded(_utf16, HtmlInputEncoding.Auto()) != direct)
            throw new InvalidOperationException("UTF-16 BOM detection changed the token stream.");
    }

    [Benchmark(Baseline = true)]
    public Task<ulong> Utf8Direct() => RunUtf8(_utf8);

    [Benchmark]
    public Task<ulong> Utf8KnownAdapter() =>
        RunEncoded(_utf8, HtmlInputEncoding.Known(Encoding.UTF8));

    [Benchmark]
    public Task<ulong> Windows1252Known() =>
        RunEncoded(_windows1252, HtmlInputEncoding.Known(Encoding.GetEncoding(1252)));

    [Benchmark]
    public Task<ulong> Windows1252AutoMeta() =>
        RunEncoded(_windows1252, HtmlInputEncoding.Auto());

    [Benchmark]
    public Task<ulong> Utf16AutoBom() => RunEncoded(_utf16, HtmlInputEncoding.Auto());

    private static async Task<ulong> RunUtf8(byte[] source)
    {
        await using var stream = new MemoryStream(source, writable: false);
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: 4 * 1024));
        var sink = new Utf8TokenizerBaselineBenchmark.FingerprintSink();
        await Utf8HtmlTokenizer.TokenizeAsync(reader, sink);
        await reader.CompleteAsync();
        return sink.Fingerprint;
    }

    private static async Task<ulong> RunEncoded(byte[] source, HtmlInputEncoding inputEncoding)
    {
        await using var stream = new MemoryStream(source, writable: false);
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: 4 * 1024));
        var sink = new Utf8TokenizerBaselineBenchmark.FingerprintSink();
        await EncodedHtmlInput.TokenizeAsync(reader, inputEncoding, sink, CancellationToken.None);
        await reader.CompleteAsync();
        return sink.Fingerprint;
    }
}
#endif
