#if NET10_0
using System.Text;
using AngleSharp.Html;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens;
using AngleSharp.ReadOnlyDom.Streaming;
using AngleSharp.Text;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[BenchmarkCategory("Utf8")]
[MemoryDiagnoser]
public class Utf8TokenizerBenchmark
{
    private byte[] _utf8 = null!;
    private readonly CountingSink _sink = new();

    [GlobalSetup]
    public void Setup()
    {
        var document = BenchmarkCorpus.LoadLargestAnonymized(2)[1];
        _utf8 = Encoding.UTF8.GetBytes(document.Html);
        Console.WriteLine($"UTF-8 tokenizer fixture: {_utf8.Length:N0} bytes.");

        var tokenizer = new Utf8HtmlTokenizer(_sink);
        tokenizer.Write(_utf8);
        tokenizer.Complete();
        Console.WriteLine($"Maximum buffered token data: {tokenizer.Counters.MaximumBufferedTokenBytes:N0} bytes.");
        _sink.Reset();
    }

    [Benchmark(Baseline = true)]
    public int DecodeThenAngleSharpTokenize()
    {
        var html = Encoding.UTF8.GetString(_utf8);
        using var tokenizer = new HtmlTokenizer(new TextSource(html), HtmlEntityProvider.ResolverExtended);
        var count = 0;
        while (true)
        {
            ref var token = ref tokenizer.GetStructToken();
            count++;
            if (token.Type == HtmlTokenType.StartTag)
            {
                var name = token.Name.ToString();
                tokenizer.State = name switch
                {
                    "title" or "textarea" => AngleSharp.Html.Parser.HtmlParseMode.RCData,
                    "style" or "xmp" or "iframe" or "noembed" or "noframes" => AngleSharp
                        .Html
                        .Parser
                        .HtmlParseMode
                        .Rawtext,
                    "script" => AngleSharp.Html.Parser.HtmlParseMode.Script,
                    "plaintext" => AngleSharp.Html.Parser.HtmlParseMode.Plaintext,
                    _ => tokenizer.State,
                };
            }
            if (token.Type == HtmlTokenType.EndOfFile)
                return count;
        }
    }

    [Benchmark]
    public int Utf8MonotonicTokenize()
    {
        _sink.Reset();
        var tokenizer = new Utf8HtmlTokenizer(_sink);
        tokenizer.Write(_utf8);
        tokenizer.Complete();
        return _sink.Events;
    }

    private sealed class CountingSink : IUtf8HtmlTokenSink
    {
        public Utf8HtmlTokenCapture Capture => Utf8HtmlTokenCapture.Text;

        public int Events { get; private set; }

        public void Reset() => Events = 0;

        public void Text(ReadOnlySpan<byte> utf8) => Events++;

        public Utf8HtmlStartTagCapture StartTag(Utf8HtmlName name)
        {
            Events++;
            return Utf8HtmlStartTagCapture.Attributes;
        }

        public bool WantsAttribute(Utf8HtmlName name) => true;

        public void Attribute(Utf8HtmlName name, ReadOnlySpan<byte> value) => Events++;

        public void StartTagEnd(bool selfClosing) => Events++;

        public void EndTag(Utf8HtmlName name) => Events++;
    }
}
#endif
