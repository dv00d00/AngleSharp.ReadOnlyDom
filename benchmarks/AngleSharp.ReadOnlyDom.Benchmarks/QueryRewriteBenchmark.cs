#if NET10_0

using System.Buffers;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[MemoryDiagnoser]
public class QueryRewriteBenchmark
{
    private static readonly QueryPlan<int> Html5Query = StreamQuery.For<int>("a").Attribute("href").Compile();
    private static readonly QueryPlan<int> QqQuery = CreateQqQuery();
    private byte[] _input = null!;
    private QueryPlan<int> _query = null!;

    [Params("html5test-full", "html5test-no-payload", "qq")]
    public string Page { get; set; } = null!;

    [GlobalSetup]
    public void Setup()
    {
        (_input, _query) = Page switch
        {
            "html5test-full" => (ReadRequiredFile("ANGLE_HTML5_FULL"), Html5Query),
            "html5test-no-payload" => (ReadRequiredFile("ANGLE_HTML5_NOPAYLOAD"), Html5Query),
            "qq" => (
                Encoding.UTF8.GetBytes(
                    BenchmarkCorpus.Load("full").Single(static document => document.Name == "qq").Html
                ),
                QqQuery
            ),
            _ => throw new InvalidOperationException($"Unknown rewrite fixture '{Page}'."),
        };

        var output = new ArrayBufferWriter<byte>(_input.Length + 1024);
        var matches = Rewrite(output);
        if (matches == 0)
            throw new InvalidOperationException($"The '{Page}' rewrite query matched no elements.");
        if (Environment.GetEnvironmentVariable("ANGLE_REWRITE_OUTPUT_DIR") is { Length: > 0 } outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllBytes(Path.Combine(outputDirectory, $"csharp-{Page}.html"), output.WrittenSpan.ToArray());
        }
        Console.WriteLine(
            $"{Page}: {_input.Length:N0} bytes, {matches:N0} rewrites, {output.WrittenCount:N0} output bytes."
        );
    }

    [Benchmark]
    public int RewriteToNewBuffer()
    {
        var output = new ArrayBufferWriter<byte>(_input.Length + 1024);
        return Rewrite(output) ^ output.WrittenCount;
    }

    private int Rewrite(ArrayBufferWriter<byte> output) =>
        _query.Rewrite(
            _input,
            output,
            0,
            static (ref int count, in Element _, ref StartTagEditor tag) =>
            {
                count++;
                tag.AppendAttribute("data-query-hit"u8, "1"u8);
            },
            Utf8InputContract.WellFormedUtf8
        );

    private static QueryPlan<int> CreateQqQuery()
    {
        var list = StreamQuery.For<int>("ul").Class("news-list");
        var card = list.Descendant("li").Attribute("dt-eid", "em_item_article");
        card.Descendant("a").Attribute("href");
        return list.Compile();
    }

    private static byte[] ReadRequiredFile(string variable)
    {
        var path = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException($"Environment variable {variable} must name the comparison fixture.");
        return File.ReadAllBytes(path);
    }
}

#endif
