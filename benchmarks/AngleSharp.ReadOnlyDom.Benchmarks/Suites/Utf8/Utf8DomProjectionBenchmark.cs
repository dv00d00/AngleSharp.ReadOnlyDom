#if NET10_0
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.Benchmarks.Support;
using AngleSharp.ReadOnlyDom.Streaming.Query;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks.Suites.Utf8;

[BenchmarkCategory("Utf8")]
[MemoryDiagnoser]
public class Utf8DomProjectionBenchmark
{
    private static readonly QueryPlan<DivFingerprintProjectionState> NativeProjection = StreamQuery
        .For<DivFingerprintProjectionState>("div")
        .OnStart(static (ref state, in element) => state.Start(in element), "id", "class")
        .OnText(static (ref state, utf8) => state.Text(utf8))
        .OnEnd(static (ref state) => state.End())
        .Compile();

    // <template> content is parsed into an inert fragment excluded from the live DOM tree, so a real HTML5 parser
    // never yields its descendant <div>s from QuerySelectorAll. The lexical StreamQuery engine has no such special
    // case (see issue #42 on the documented lexical-topology boundary), so it matches them like any other div.
    // Stripped here so both projection paths compare against equivalent, unambiguous markup.
    private static readonly Regex TemplateElement = new(
        @"<template\b[^>]*>.*?</template\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private readonly HtmlParser _parser = new();
    private byte[] _utf8 = null!;
    private ulong _expected;

    [GlobalSetup]
    public void Setup()
    {
        var document = BenchmarkCorpus.LoadLargestAnonymized(2)[1];
        _utf8 = Encoding.UTF8.GetBytes(TemplateElement.Replace(document.Html, ""));
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
        var fingerprint = DivFingerprint.OffsetBasis;
        var matches = 0;
        foreach (var element in document.QuerySelectorAll("div"))
        {
            DivFingerprint.AppendUInt64(
                ref fingerprint,
                DivFingerprint.HashChars(element.GetAttribute("id") ?? string.Empty)
            );
            DivFingerprint.AppendUInt64(
                ref fingerprint,
                DivFingerprint.HashChars(element.GetAttribute("class") ?? string.Empty)
            );
            DivFingerprint.AppendUInt64(ref fingerprint, DivFingerprint.HashChars(element.TextContent));
            matches++;
        }
        DivFingerprint.AppendUInt64(ref fingerprint, (ulong)matches);
        return fingerprint;
    }

    [Benchmark]
    public ulong NativeUtf8Project()
    {
        using var state = new DivFingerprintProjectionState();
        NativeProjection.Execute(_utf8, state, Utf8InputContract.WellFormedUtf8);
        return state.Fingerprint;
    }
}
#endif
