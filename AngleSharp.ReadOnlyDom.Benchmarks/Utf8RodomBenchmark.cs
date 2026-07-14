#if NET10_0
using System.Text;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.Compact;
using AngleSharp.ReadOnlyDom.Streaming;
using AngleSharp.ReadOnlyDom.Compact.Experimental;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[MemoryDiagnoser]
public class Utf8RodomBenchmark
{
    private const int NetworkChunkSize = 4096;

    private readonly HtmlParser _parser = CompactParser.CreateParser();
    private byte[] _utf8 = null!;
    private ulong _expected;

    [GlobalSetup]
    public void Setup()
    {
        var document = BenchmarkCorpus.LoadLargestAnonymized(2)[1];
        _utf8 = Encoding.UTF8.GetBytes(document.Html);
        var html = Encoding.UTF8.GetString(_utf8);
        using var expectedDocument = _parser.ParseCompactDocument(html);
        _expected = Fingerprint(expectedDocument, out var expectedMatches);
        var resident = ResidentBytesThenCompact();
        var bounded = BoundedStreamThenCompact();

        if (resident != _expected || bounded != _expected)
            throw new InvalidOperationException(
                $"UTF-8 RODOM benchmark implementations disagree. "
                    + $"compact={_expected:X16}/{expectedMatches}, "
                    + $"resident={resident:X16}, bounded={bounded:X16}."
            );

        Console.WriteLine($"UTF-8 RODOM fixture: {_utf8.Length:N0} bytes, fingerprint {_expected:X16}.");
    }

    [Benchmark(Baseline = true)]
    public ulong DecodeThenCompact()
    {
        var html = Encoding.UTF8.GetString(_utf8);
        using var document = _parser.ParseCompactDocument(html);
        return Fingerprint(document);
    }

    [Benchmark]
    public ulong ResidentBytesThenCompact()
    {
        using var document = _parser.ParseCompactDocument(_utf8.AsMemory(), Encoding.UTF8);
        return Fingerprint(document);
    }

    [Benchmark]
    public ulong BoundedStreamThenCompact()
    {
        using var document = _parser
            .ParseCompactDocumentAsync(
                new NetworkReadStream(_utf8, NetworkChunkSize),
                HtmlStreamSourceMode.Streaming,
                Encoding.UTF8
            )
            .GetAwaiter()
            .GetResult();
        return Fingerprint(document);
    }

    private static ulong Fingerprint(CompactDocument document) => Fingerprint(document, out _);

    private static ulong Fingerprint(CompactDocument document, out int matches)
    {
        var fingerprint = Utf8DivFingerprintFold.OffsetBasis;
        matches = 0;
        foreach (var element in document.Elements("div"))
        {
            var sink = new HashSink();
            var id = Utf8DivFingerprintFold.HashChars(element.Attr("id"));
            var classes = Utf8DivFingerprintFold.HashChars(element.Attr("class"));
            element.WriteText(ref sink);
            Utf8DivFingerprintFold.AppendUInt64(ref fingerprint, id);
            Utf8DivFingerprintFold.AppendUInt64(ref fingerprint, classes);
            Utf8DivFingerprintFold.AppendUInt64(ref fingerprint, sink.Value);
            matches++;
        }
        Utf8DivFingerprintFold.AppendUInt64(ref fingerprint, (ulong)matches);
        return fingerprint;
    }

    private struct HashSink : ISpanSink
    {
        public HashSink()
        {
            Value = 14695981039346656037UL;
        }

        public ulong Value;

        public void Append(ReadOnlySpan<char> value)
        {
            foreach (var character in value)
            {
                Value ^= character;
                Value *= Utf8DivFingerprintFold.Prime;
            }
        }
    }

    private sealed class NetworkReadStream(byte[] source, int maxReadSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var length = Math.Min(Math.Min(buffer.Length, maxReadSize), source.Length - _position);
            if (length <= 0)
                return 0;

            source.AsSpan(_position, length).CopyTo(buffer);
            _position += length;
            return length;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(Read(buffer.Span));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
#endif
