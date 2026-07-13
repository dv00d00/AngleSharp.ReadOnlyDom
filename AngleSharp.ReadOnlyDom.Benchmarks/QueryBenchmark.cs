#if NET10_0
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.CompactPrototype;
using AngleSharp.ReadOnlyDom.Html;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[MemoryDiagnoser]
[GcServer(true)]
public class QueryBenchmark
{
    private static readonly HtmlParserOptions Options = new()
    {
        SkipComments = true,
        SkipProcessingInstructions = true,
        IsKeepingSourceReferences = false,
    };

    private readonly HtmlParser _angleSharpParser = new(Options);
    private readonly HtmlParser _readOnlyParser = new(
        Options,
        ReadOnlyParser.CreateContext(ReadOnlyMetadataProfile.Minimal)
    );
    private readonly CompactParserSession _arenaParser = new(parserOptions: Options);

    private IDocument _angleSharp = null!;
    private IReadOnlyDocument _readOnly = null!;
    private CompactDocument _arena = null!;
    private int _expected;

    [Params(5_000, 50_000)]
    public int Noise { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var html = Bake(Noise, out _expected);
        _angleSharp = _angleSharpParser.ParseDocument(html);
        _readOnly = _readOnlyParser.ParseReadOnlyDocument(html);
        _arena = _arenaParser.Parse(html);

        var angle = AngleSharp();
        var readOnly = ReadOnly();
        var readOnlyFast = ReadOnlyFast();
        var descendants = ArenaDescendants();
        var descendantsPre = ArenaDescendantsPre();
        var pushdown = ArenaPushdown();
        if (
            angle != _expected
            || readOnly != _expected
            || readOnlyFast != _expected
            || descendants != _expected
            || descendantsPre != _expected
            || pushdown != _expected
        )
        {
            throw new InvalidOperationException(
                $"div.error count disagrees: expected={_expected}, angleSharp={angle}, readOnly={readOnly}, "
                    + $"readOnlyFast={readOnlyFast}, descendants={descendants}, descendantsPre={descendantsPre}, "
                    + $"pushdown={pushdown}."
            );
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _angleSharp.Dispose();
        _readOnly.Dispose();
        _arena.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int AngleSharp() => _angleSharp.QuerySelectorAll("div.error").Length;

    [Benchmark]
    public int ReadOnly()
    {
        var count = 0;
        foreach (var node in _readOnly.AllDescendants())
        {
            if (node.TagClass("div", "error"))
                count++;
        }
        return count;
    }

    [Benchmark]
    public int ReadOnlyFast() => _readOnly.CountTagClass("div", "error");

    [Benchmark]
    public int ArenaDescendants()
    {
        var count = 0;
        foreach (var node in _arena.Descendants())
        {
            if (node.Is("div") && node.HasClass("error"))
                count++;
        }
        return count;
    }

    [Benchmark]
    public int ArenaDescendantsPre()
    {
        var divId = _arena.Name("div");
        var count = 0;
        foreach (var node in _arena.Descendants())
        {
            if (node.Is(divId) && node.HasClass("error"))
                count++;
        }
        return count;
    }

    [Benchmark]
    public int ArenaPushdown() => _arena.Elements("div").WithClass("error").Count();

    private static string Bake(int noise, out int errors)
    {
        var builder = new StringBuilder();
        builder.Append("<!doctype html><html><head><title>q</title></head><body>");
        errors = 0;
        for (var i = 0; i < noise; i++)
        {
            builder
                .Append("<div class=\"row r")
                .Append(i % 13)
                .Append("\"><span>cell ")
                .Append(i)
                .Append("</span> text ")
                .Append(i)
                .Append("</div>");
            if ((i % 500) == 0)
            {
                builder.Append("<div class=\"card error\"><span>boom ").Append(i).Append("</span></div>");
                errors++;
            }
        }
        builder.Append("</body></html>");
        return builder.ToString();
    }
}
#endif
