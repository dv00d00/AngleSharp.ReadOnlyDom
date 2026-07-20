#if NET10_0
using System.IO.Pipelines;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

public enum Utf8BaselineWorkload
{
    Typical,
    Malformed,
    RawText,
    EntityHeavy,
    LongToken,
    CompactNames,
    FallbackNames,
    MixedCaseDuplicates,
}

[MemoryDiagnoser]
public class Utf8TokenizerBaselineBenchmark
{
    private const int SegmentSize = 4 * 1024;
    private byte[] _utf8 = null!;
    private readonly FingerprintSink _sink = new();

    [ParamsSource(nameof(Workloads))]
    public Utf8BaselineWorkload Workload { get; set; }

    public IEnumerable<Utf8BaselineWorkload> Workloads()
    {
        var selected = Environment.GetEnvironmentVariable("ANGLE_UTF8_TOKENIZER_WORKLOADS");
        if (string.IsNullOrWhiteSpace(selected))
            return Enum.GetValues<Utf8BaselineWorkload>();

        return selected
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static value => Enum.Parse<Utf8BaselineWorkload>(value, ignoreCase: true))
            .Distinct()
            .ToArray();
    }

    [GlobalSetup]
    public async Task Setup()
    {
        _utf8 = Utf8TokenizerBaselineCorpus.Create(Workload);
        _sink.Reset();
        var counters = TokenizeContiguous(_utf8, _sink);
        var contiguousFingerprint = _sink.Fingerprint;
        _sink.Reset();
        var (segmentedFingerprint, segmentedCounters) = await TokenizeSegmented(_utf8, _sink);
        if (segmentedFingerprint != contiguousFingerprint || segmentedCounters.BytesConsumed != counters.BytesConsumed)
            throw new InvalidOperationException($"Segmented {Workload} tokenization differs from contiguous input.");
        Console.WriteLine(
            $"UTF-8 baseline {Workload}: {_utf8.Length:N0} bytes; maximum buffered token: "
                + $"{counters.MaximumBufferedTokenBytes:N0} bytes."
        );
    }

    [IterationSetup]
    public void Reset() => _sink.Reset();

    [Benchmark(Baseline = true)]
    public ulong ContiguousMemory()
    {
        TokenizeContiguous(_utf8, _sink);
        return _sink.Fingerprint;
    }

    [Benchmark]
    public async Task<ulong> SegmentedPipeReader()
    {
        var (fingerprint, _) = await TokenizeSegmented(_utf8, _sink);
        return fingerprint;
    }

    private static async Task<(ulong Fingerprint, Utf8HtmlTokenizerCounters Counters)> TokenizeSegmented(
        byte[] utf8,
        FingerprintSink sink
    )
    {
        var pipe = new Pipe(
            new PipeOptions(
                pauseWriterThreshold: long.MaxValue,
                resumeWriterThreshold: long.MaxValue,
                minimumSegmentSize: SegmentSize,
                useSynchronizationContext: false
            )
        );

        for (var offset = 0; offset < utf8.Length; offset += SegmentSize)
        {
            var length = Math.Min(SegmentSize, utf8.Length - offset);
            utf8.AsSpan(offset, length).CopyTo(pipe.Writer.GetSpan(length));
            pipe.Writer.Advance(length);
            await pipe.Writer.FlushAsync();
        }
        await pipe.Writer.CompleteAsync();

        var counters = await Utf8HtmlTokenizer.TokenizeAsync(pipe.Reader, sink);
        await pipe.Reader.CompleteAsync();
        return (sink.Fingerprint, counters);
    }

    internal static Utf8HtmlTokenizerCounters TokenizeContiguous(byte[] utf8, IUtf8HtmlTokenSink sink)
    {
        var tokenizer = new Utf8HtmlTokenizer(sink);
        tokenizer.Write(utf8);
        tokenizer.Complete();
        return tokenizer.Counters;
    }

    internal sealed class FingerprintSink : IUtf8HtmlTokenSink
    {
        public Utf8HtmlTokenCapture Capture => Utf8HtmlTokenCapture.Text;

        private const ulong Offset = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;
        private bool _inText;

        public ulong Fingerprint { get; private set; } = Offset;

        public void Reset()
        {
            Fingerprint = Offset;
            _inText = false;
        }

        public void Text(ReadOnlySpan<byte> utf8)
        {
            if (!_inText)
                AddMarker(1);
            Add(utf8);
            _inText = true;
        }

        public Utf8HtmlStartTagCapture StartTag(Utf8HtmlName name)
        {
            AddSemanticToken(2, name);
            return Utf8HtmlStartTagCapture.Attributes;
        }

        public bool WantsAttribute(Utf8HtmlName name) => true;

        public void Attribute(Utf8HtmlName name, ReadOnlySpan<byte> value)
        {
            AddSemanticToken(3, name);
            Add(value);
        }

        public void StartTagEnd(bool selfClosing) => AddMarker(selfClosing ? (byte)5 : (byte)4);

        public void EndTag(Utf8HtmlName name) => AddSemanticToken(6, name);

        public void Comment(ReadOnlySpan<byte> value) => AddToken(7, value);

        public void Doctype(Utf8DoctypeToken token) => AddToken(8, token.Name);

        public void EndOfFile() => AddMarker(9);

        private void AddToken(byte marker, ReadOnlySpan<byte> value)
        {
            AddMarker(marker);
            Add(value);
        }

        private void AddSemanticToken(byte marker, Utf8HtmlName name)
        {
            AddMarker(marker);
            foreach (var item in name.Verbatim)
            {
                var semantic = (uint)(item - (byte)'A') <= 'Z' - 'A' ? (byte)(item | 0x20) : item;
                Fingerprint = (Fingerprint ^ semantic) * Prime;
            }
        }

        private void AddMarker(byte marker)
        {
            _inText = false;
            Fingerprint = (Fingerprint ^ marker) * Prime;
        }

        private void Add(ReadOnlySpan<byte> value)
        {
            foreach (var item in value)
                Fingerprint = (Fingerprint ^ item) * Prime;
        }
    }
}

internal static class Utf8TokenizerBaselineCorpus
{
    public const int PayloadBytes = 256 * 1024;

    public static byte[] Create(Utf8BaselineWorkload workload) =>
        workload switch
        {
            Utf8BaselineWorkload.Typical => RepeatToExactSize(
                "<article class='card'><h2>Measured title</h2><p>Ordinary UTF-8 text.</p></article>"
            ),
            Utf8BaselineWorkload.Malformed => RepeatToExactSize(
                "<table><p><b broken='x><tr><td>cell</table><!-- open <i attr=&bad"
            ),
            Utf8BaselineWorkload.RawText => RepeatToExactSize(
                "<script>if (a < b) { value = '<not-a-tag>'; }</script><style>.x::before{content:'<&';}</style>"
            ),
            Utf8BaselineWorkload.EntityHeavy => RepeatToExactSize(
                "<p title='&amp;&notin;&#x1F600;'>&lt;&gt;&quot;&apos;&copy;&#169;&notit;</p>"
            ),
            Utf8BaselineWorkload.LongToken => CreateLongToken(),
            Utf8BaselineWorkload.CompactNames => RepeatToExactSize(
                "<article id='item' class='card' href='/item' src='image' alt='preview' title='measured' "
                    + "name='entry' type='example' lang='en' width='320' height='200' rel='next' value='42' "
                    + "content='payload'>ordinary text</article>"
            ),
            Utf8BaselineWorkload.FallbackNames => RepeatToExactSize(
                "<custom-element data-record='1' aria-label='item' http-equiv='refresh' accept-charset='utf-8' "
                    + "data-alpha='a' data-beta='b' data-gamma='c' data-delta='d' data-epsilon='e' "
                    + "data-zeta='f' data-eta='g' data-theta='h' data-iota='i' data-kappa='j' "
                    + "data-lambda='k' data-mu='l' data-nu='m' data-xi='n'>ordinary text</custom-element>"
            ),
            Utf8BaselineWorkload.MixedCaseDuplicates => RepeatToExactSize(
                "<ArTiClE ID='first' id='ignored' CLASS='card' class='ignored' "
                    + "DaTa-Key='one' data-key='ignored' TITLE='title' title='ignored'>ordinary text</ArTiClE>"
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(workload)),
        };

    private static byte[] RepeatToExactSize(string fragment)
    {
        var source = Encoding.UTF8.GetBytes(fragment);
        var result = new byte[PayloadBytes];
        var offset = 0;
        while (source.Length <= result.Length - offset)
        {
            source.CopyTo(result, offset);
            offset += source.Length;
        }
        result.AsSpan(offset).Fill((byte)' ');
        return result;
    }

    private static byte[] CreateLongToken()
    {
        var prefix = "<div data-payload='"u8;
        var suffix = "'>tail</div>"u8;
        var result = new byte[PayloadBytes];
        prefix.CopyTo(result);
        result.AsSpan(prefix.Length, result.Length - prefix.Length - suffix.Length).Fill((byte)'x');
        suffix.CopyTo(result.AsSpan(result.Length - suffix.Length));
        return result;
    }
}
#endif
