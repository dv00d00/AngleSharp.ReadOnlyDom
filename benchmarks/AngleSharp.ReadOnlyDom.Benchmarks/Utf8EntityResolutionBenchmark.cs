#if NET10_0

using System.Text;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

public enum Utf8EntityProfile
{
    Plain,
    CommonNamed,
    LongNamed,
    Numeric,
    FailedNamed,
    Mixed,
}

[MemoryDiagnoser]
public class Utf8EntityResolutionBenchmark
{
    private const int PayloadBytes = 256 * 1024;
    private readonly LengthSink _sink = new();
    private byte[] _input = null!;

    [ParamsAllValues]
    public Utf8EntityProfile Profile { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _input = RepeatToExactSize(Profile switch
        {
            Utf8EntityProfile.Plain => "<p title='alpha beta'>alpha beta</p>",
            Utf8EntityProfile.CommonNamed =>
                "<p title='&amp;&lt;&gt;&quot;'>&amp;&lt;&gt;&quot;</p>",
            Utf8EntityProfile.LongNamed =>
                "<p title='&CounterClockwiseContourIntegral;&NotNestedGreaterGreater;&nparsl;'>&CounterClockwiseContourIntegral;&NotNestedGreaterGreater;&nparsl;</p>",
            Utf8EntityProfile.Numeric =>
                "<p title='&#169;&#x1F600;&#0;&#xD800;&#x80;'>&#169;&#x1F600;&#0;&#xD800;&#x80;</p>",
            Utf8EntityProfile.FailedNamed =>
                "<p title='&notit;&ampx;&CounterClockwiseContourIntegrall;&bogusreference;'>&notit;&ampx;&CounterClockwiseContourIntegrall;&bogusreference;</p>",
            Utf8EntityProfile.Mixed =>
                "<p title='plain &amp; &#169; &CounterClockwiseContourIntegral; &bogusreference;'>plain &amp; &#169; &CounterClockwiseContourIntegral; &bogusreference;</p>",
            _ => throw new ArgumentOutOfRangeException(),
        });

        var checksum = Tokenize();
        if (checksum == 0)
            throw new InvalidOperationException($"The {Profile} entity fixture produced an empty checksum.");
    }

    [Benchmark]
    public int Tokenize()
    {
        _sink.Reset();
        var tokenizer = new Utf8HtmlTokenizer(_sink);
        tokenizer.Write(_input);
        tokenizer.Complete();
        return _sink.Checksum;
    }

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

    private sealed class LengthSink : IUtf8HtmlTokenSink
    {
        public Utf8HtmlTokenCapture Capture => Utf8HtmlTokenCapture.Text;
        public int Checksum { get; private set; }

        public void Reset() => Checksum = 0;

        public void Text(ReadOnlySpan<byte> utf8) => Checksum += utf8.Length;

        public Utf8HtmlStartTagCapture StartTag(Utf8HtmlName name)
        {
            Checksum += name.Verbatim.Length;
            return Utf8HtmlStartTagCapture.Attributes;
        }

        public bool WantsAttribute(Utf8HtmlName name) => true;

        public void Attribute(Utf8HtmlName name, ReadOnlySpan<byte> value) =>
            Checksum += name.Verbatim.Length + value.Length;

        public void StartTagEnd(bool selfClosing) => Checksum += selfClosing ? 2 : 1;

        public void EndTag(Utf8HtmlName name) => Checksum += name.Verbatim.Length;
    }
}

#endif
