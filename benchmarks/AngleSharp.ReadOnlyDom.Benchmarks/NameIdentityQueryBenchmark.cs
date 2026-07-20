#if NET10_0

using System.Buffers;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

public enum NameIdentityWorkload
{
    Compact,
    Fallback,
    LongFallback,
    MixedCaseDuplicates,
}

[MemoryDiagnoser]
public class NameIdentityQueryBenchmark
{
    private static readonly QueryPlan<int> CompactMatch = CreateCompactQuery(countMatches: true);
    private static readonly QueryPlan<int> CompactRewrite = CreateCompactQuery(countMatches: false);
    private static readonly QueryPlan<int> FallbackMatch = CreateFallbackQuery(countMatches: true);
    private static readonly QueryPlan<int> FallbackRewrite = CreateFallbackQuery(countMatches: false);
    private static readonly QueryPlan<int> LongFallbackMatch = CreateLongFallbackQuery(countMatches: true);
    private static readonly QueryPlan<int> LongFallbackRewrite = CreateLongFallbackQuery(countMatches: false);
    private static readonly QueryPlan<int> MixedMatch = CreateMixedQuery(countMatches: true);
    private static readonly QueryPlan<int> MixedRewrite = CreateMixedQuery(countMatches: false);

    private byte[] _input = null!;
    private QueryPlan<int> _match = null!;
    private QueryPlan<int> _rewrite = null!;
    private int _expectedMatches;

    [Params(
        NameIdentityWorkload.Compact,
        NameIdentityWorkload.Fallback,
        NameIdentityWorkload.LongFallback,
        NameIdentityWorkload.MixedCaseDuplicates
    )]
    public NameIdentityWorkload Workload { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var (fragment, match, rewrite) = Workload switch
        {
            NameIdentityWorkload.Compact => (
                "<article id='item' class='card' title='measured' content='payload'>ordinary text</article>",
                CompactMatch,
                CompactRewrite
            ),
            NameIdentityWorkload.Fallback => (
                "<custom-element data-record='1' aria-label='item' http-equiv='refresh'>ordinary text</custom-element>",
                FallbackMatch,
                FallbackRewrite
            ),
            NameIdentityWorkload.LongFallback => (
                "<custom-element DaTa-Customer-Record-Id='1' "
                    + "ArIa-AcTiVeDeScEnDaNt='item' "
                    + "ArIa-MuLtIsElEcTaBlE='true'>ordinary text</custom-element>",
                LongFallbackMatch,
                LongFallbackRewrite
            ),
            NameIdentityWorkload.MixedCaseDuplicates => (
                "<ArTiClE ID='first' id='ignored' CLASS='card' class='ignored' "
                    + "DaTa-Key='one' data-key='ignored' TITLE='title' title='ignored'>ordinary text</ArTiClE>",
                MixedMatch,
                MixedRewrite
            ),
            _ => throw new ArgumentOutOfRangeException(),
        };

        (_input, _expectedMatches) = CreateFixture(fragment);
        _match = match;
        _rewrite = rewrite;

        if (MatchOnly() != _expectedMatches)
            throw new InvalidOperationException($"{Workload} match validation failed.");

        var output = new ArrayBufferWriter<byte>(_input.Length + 1024);
        if (Rewrite(output) != _expectedMatches)
            throw new InvalidOperationException($"{Workload} rewrite validation failed.");
    }

    [Benchmark(Baseline = true)]
    public int MatchOnly() => _match.Execute(_input, 0, Utf8InputContract.WellFormedUtf8);

    [Benchmark]
    public int RewriteToNewBuffer()
    {
        var output = new ArrayBufferWriter<byte>(_input.Length + 1024);
        return Rewrite(output) ^ output.WrittenCount;
    }

    private int Rewrite(ArrayBufferWriter<byte> output) =>
        _rewrite.Rewrite(
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

    private static QueryPlan<int> CreateCompactQuery(bool countMatches)
    {
        var match = StreamQuery
            .For<int>("article")
            .Attribute("id", "item")
            .Attribute("class", "card")
            .Attribute("title", "measured")
            .Attribute("content", "payload");
        if (countMatches)
            match.OnStart(static (ref int count, in Element _) => count++);
        return match.Compile();
    }

    private static QueryPlan<int> CreateFallbackQuery(bool countMatches)
    {
        var match = StreamQuery
            .For<int>("custom-element")
            .Attribute("data-record", "1")
            .Attribute("aria-label", "item")
            .Attribute("http-equiv", "refresh");
        if (countMatches)
            match.OnStart(static (ref int count, in Element _) => count++);
        return match.Compile();
    }

    private static QueryPlan<int> CreateLongFallbackQuery(bool countMatches)
    {
        var match = StreamQuery
            .For<int>("custom-element")
            .Attribute("data-customer-record-id", "1")
            .Attribute("aria-activedescendant", "item")
            .Attribute("aria-multiselectable", "true");
        if (countMatches)
            match.OnStart(static (ref int count, in Element _) => count++);
        return match.Compile();
    }

    private static QueryPlan<int> CreateMixedQuery(bool countMatches)
    {
        var match = StreamQuery
            .For<int>("article")
            .Attribute("id", "first")
            .Attribute("class", "card")
            .Attribute("data-key", "one")
            .Attribute("title", "title");
        if (countMatches)
            match.OnStart(static (ref int count, in Element _) => count++);
        return match.Compile();
    }

    private static (byte[] Input, int Copies) CreateFixture(string fragment)
    {
        const int targetBytes = 256 * 1024;
        var source = Encoding.UTF8.GetBytes(fragment);
        var copies = Math.Max(1, targetBytes / source.Length);
        var input = new byte[source.Length * copies];
        for (var offset = 0; offset < input.Length; offset += source.Length)
            source.CopyTo(input, offset);
        return (input, copies);
    }
}

#endif
