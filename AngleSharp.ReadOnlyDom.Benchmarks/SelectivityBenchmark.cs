#if NET10_0
using System.Text;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.CompactPrototype;
using AngleSharp.ReadOnlyDom.Html;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

// Isolated selection cost (no parse, no extraction): the document is parsed once in Setup and held,
// then each benchmark just counts a SPARSE tag (<form>) buried in noise. This is the regime where the
// columnar SIMD scan should win: the target is a tiny fraction of nodes, so a vectorized IndexOf over
// the name-id column skips huge non-matching runs, while the scalar loop reconstructs every node and the
// read-only DOM walks every object. Contrast with FullFlowBenchmark, which is extraction-bound and hides this.
[MemoryDiagnoser]
[GcServer(true)]
public class SelectivityBenchmark
{
    private static readonly HtmlParserOptions Options = new()
    {
        SkipComments = true,
        SkipProcessingInstructions = true,
        IsKeepingSourceReferences = false,
    };

    private readonly HtmlParser _readOnlyParser = new(
        Options,
        ReadOnlyParser.CreateContext(ReadOnlyMetadataProfile.Minimal)
    );
    private readonly CompactParserSession _arenaParser = new(parserOptions: Options);

    private IReadOnlyDocument _readOnly = null!;
    private CompactDocument _arena = null!;
    private int _forms;

    [Params(5_000, 50_000)]
    public int Noise { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var html = Bake(Noise, out _forms);
        _readOnly = _readOnlyParser.ParseReadOnlyDocument(html);
        _arena = _arenaParser.Parse(html);

        var readOnly = ReadOnlyFind();
        var scalar = ArenaScalarFind();
        var simd = ArenaSimdFind();
        if (readOnly != _forms || scalar != _forms || simd != _forms)
        {
            throw new InvalidOperationException(
                $"form count disagrees: expected={_forms}, readOnly={readOnly}, scalar={scalar}, simd={simd}."
            );
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _readOnly.Dispose();
        _arena.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int ReadOnlyFind()
    {
        var count = 0;
        foreach (var node in _readOnly.AllDescendants())
        {
            if (node.Tag("form"))
                count++;
        }
        return count;
    }

    [Benchmark]
    public int ArenaScalarFind()
    {
        var formId = _arena.FindNameId("form");
        if (formId == ushort.MaxValue)
            return 0;
        var count = 0;
        for (var handle = 0; handle < _arena.NodeCount; handle++)
        {
            var node = _arena.GetNode(handle);
            if (node.Kind == CompactNodeKind.Element && node.NameId == formId)
                count++;
        }
        return count;
    }

    [Benchmark]
    public int ArenaSimdFind() => _arena.Elements("form").Count();

    private static string Bake(int noise, out int forms)
    {
        var builder = new StringBuilder();
        builder.Append("<!doctype html><html><head><title>noise</title></head><body>");
        forms = 0;
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
                builder.Append("<form action=\"/f").Append(i).Append("\"><input name=\"q\"></form>");
                forms++;
            }
        }
        builder.Append("</body></html>");
        return builder.ToString();
    }
}
#endif
