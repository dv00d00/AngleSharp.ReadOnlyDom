#if NET10_0
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.Streaming;
using AngleSharp.ReadOnlyDom.Compact.Experimental;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[MemoryDiagnoser]
public class Utf8DomProjectionBenchmark
{
    private readonly HtmlParser _parser = new();
    private byte[] _utf8 = null!;
    private ulong _expected;

    [GlobalSetup]
    public void Setup()
    {
        var document = BenchmarkCorpus.LoadLargestAnonymized(2)[1];
        _utf8 = Encoding.UTF8.GetBytes(document.Html);
        _expected = DecodeParseAndProject();
        var native = NativeUtf8Project();
        if (native != _expected)
            throw new InvalidOperationException(
                $"Native UTF-8 projection disagrees with AngleSharp DOM: "
                    + $"dom={_expected:X16}, native={native:X16}."
            );
        Console.WriteLine($"UTF-8 DOM projection fixture: {_utf8.Length:N0} bytes, fingerprint {_expected:X16}.");
    }

    [Benchmark(Baseline = true)]
    public ulong DecodeParseAndProject()
    {
        var html = Encoding.UTF8.GetString(_utf8);
        using var document = _parser.ParseDocument(html);
        var fingerprint = Utf8DivFingerprintFold.OffsetBasis;
        var matches = 0;
        foreach (var element in document.QuerySelectorAll("div"))
        {
            Utf8DivFingerprintFold.AppendUInt64(
                ref fingerprint,
                Utf8DivFingerprintFold.HashChars(element.GetAttribute("id") ?? string.Empty)
            );
            Utf8DivFingerprintFold.AppendUInt64(
                ref fingerprint,
                Utf8DivFingerprintFold.HashChars(element.GetAttribute("class") ?? string.Empty)
            );
            Utf8DivFingerprintFold.AppendUInt64(ref fingerprint, Utf8DivFingerprintFold.HashChars(element.TextContent));
            matches++;
        }
        Utf8DivFingerprintFold.AppendUInt64(ref fingerprint, (ulong)matches);
        return fingerprint;
    }

    [Benchmark]
    public ulong NativeUtf8Project()
    {
        using var fold = new Utf8DivFingerprintFold();
        var tokenizer = new Utf8HtmlTokenizer(fold);
        tokenizer.Write(_utf8);
        tokenizer.Complete();
        return fold.Fingerprint;
    }
}
#endif
