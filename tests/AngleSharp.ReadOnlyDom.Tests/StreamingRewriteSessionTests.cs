#if NET10_0
using System.Buffers;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming;
using AngleSharp.ReadOnlyDom.Streaming.Query;
using AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;

namespace AngleSharp.Readonly.Tests;

public sealed class StreamingRewriteSessionTests
{
    private static QueryPlan<int> CreatePlan()
    {
        var root = StreamQuery.For<int>("main");
        root.Descendant("a").Attribute("href");
        return root.Compile();
    }

    private static void Edit(ref int count, in Element _, ref StartTagEditor tag)
    {
        count++;
        tag.AppendAttribute("data-q"u8, "1"u8);
    }

    private static byte[] RewriteWholeBuffer(QueryPlan<int> plan, byte[] source, out int matches)
    {
        var output = new ArrayBufferWriter<byte>();
        matches = plan.Rewrite(source, output, 0, Edit, Utf8InputContract.WellFormedUtf8);
        return output.WrittenSpan.ToArray();
    }

    private static byte[] RewriteStreaming(
        QueryPlan<int> plan,
        byte[] source,
        IEnumerable<int> chunkLengths,
        out int matches,
        Utf8InputContract inputContract = Utf8InputContract.WellFormedUtf8
    )
    {
        var output = new ArrayBufferWriter<byte>();
        using var session = plan.CreateRewriteSession(0, output, Edit, inputContract);
        var offset = 0;
        foreach (var length in chunkLengths)
        {
            var slice = Math.Min(length, source.Length - offset);
            session.Write(source.AsSpan(offset, slice));
            offset += slice;
            if (offset == source.Length)
                break;
        }
        if (offset < source.Length)
            session.Write(source.AsSpan(offset));
        matches = session.Complete();
        return output.WrittenSpan.ToArray();
    }

    private static IEnumerable<int> Repeat(int size)
    {
        while (true)
            yield return size;
    }

    private static IEnumerable<int> Seeded(int seed)
    {
        var random = new Random(seed);
        while (true)
            yield return random.Next(1, 37);
    }

    // Edits at offset zero, back to back, self-closing (edited twice by the double handler below),
    // quoted '>', character references, CRLF inside a tag, raw text with fake tags, comments,
    // non-ASCII text, and a tail without matches.
    private static readonly string[] Documents =
    [
        "<main><a href='x'>t</a></main>",
        "<main>é<!--keep--><a href='x'>text</a><a href=y /></main>",
        "<main><a href='x'>t</a><a href=y /><a href=z >u</a><b>rest</b>é</main>",
        "<main><a\r\nhref='x'>t</a>\r\nplain</main>",
        "<main><a href='a&amp;b'>t</a><a href='x>y'>u</a></main>",
        "<main><script>var a = '<a href=q>'</script><a href=z>t</a></main>",
        "<main><div class='no match'><span>deep</span></div><a href='x'>t</a>tail text</main>",
        "<main>π – текст<a href='ünïcode'>ünïcode</a>π – текст</main>",
        "<main><a href='x'>t</a><a href=",
        "<main><!doctype ignored><![CDATA[not real]]><a href=x>t</a></main>",
        "no tags at all, just a long run of ordinary text that publishes eagerly",
    ];

    [Test]
    public async Task StreamingOutputMatchesWholeBufferAcrossChunkings()
    {
        var plan = CreatePlan();
        int[] fixedSizes = [1, 2, 3, 7, 16, 64, 4096];
        int[] seeds = [214748, 12345, 8675309];

        foreach (var document in Documents)
        {
            var source = Encoding.UTF8.GetBytes(document);
            var expected = RewriteWholeBuffer(plan, source, out var expectedMatches);

            foreach (var size in fixedSizes)
            {
                var actual = RewriteStreaming(plan, source, Repeat(size), out var matches);
                await Assert.That(actual.SequenceEqual(expected)).IsTrue();
                await Assert.That(matches).IsEqualTo(expectedMatches);
            }
            foreach (var seed in seeds)
            {
                var actual = RewriteStreaming(plan, source, Seeded(seed), out var matches);
                await Assert.That(actual.SequenceEqual(expected)).IsTrue();
                await Assert.That(matches).IsEqualTo(expectedMatches);
            }
        }
    }

    [Test]
    public async Task MultipleEditsPerTagMatchTheWholeBufferPath()
    {
        var plan = CreatePlan();
        var source = "<main><a href='x'>t</a><a href=y /></main>"u8.ToArray();

        static void DoubleEdit(ref int count, in Element _, ref StartTagEditor tag)
        {
            count++;
            tag.AppendAttribute("data-q"u8, "1"u8);
            tag.AppendAttribute("data-r"u8, "&2\""u8);
        }

        var expectedOutput = new ArrayBufferWriter<byte>();
        var expected = plan.Rewrite(source, expectedOutput, 0, DoubleEdit, Utf8InputContract.WellFormedUtf8);

        for (var size = 1; size <= source.Length; size++)
        {
            var output = new ArrayBufferWriter<byte>();
            using var session = plan.CreateRewriteSession(0, output, DoubleEdit, Utf8InputContract.WellFormedUtf8);
            for (var offset = 0; offset < source.Length; offset += size)
                session.Write(source.AsSpan(offset, Math.Min(size, source.Length - offset)));
            var matches = session.Complete();

            await Assert.That(matches).IsEqualTo(expected);
            await Assert.That(output.WrittenSpan.SequenceEqual(expectedOutput.WrittenSpan)).IsTrue();
        }
    }

    [Test]
    public async Task PublishesEagerlyBeforeComplete()
    {
        var plan = CreatePlan();
        var output = new ArrayBufferWriter<byte>();
        using var session = plan.CreateRewriteSession(0, output, Edit, Utf8InputContract.WellFormedUtf8);

        session.Write("<main><a href='x'>t</a>finished text"u8);
        // Everything written so far is final - no start tag is open - so it must already be published.
        await Assert
            .That(Encoding.UTF8.GetString(output.WrittenSpan))
            .IsEqualTo("<main><a href='x' data-q=\"1\">t</a>finished text");

        session.Write("<a hre"u8);
        var beforeOpenTag = output.WrittenCount;
        session.Write("f='y'"u8);
        // The open start tag can still be edited; nothing new may be published.
        await Assert.That(output.WrittenCount).IsEqualTo(beforeOpenTag);

        session.Write(">t</a></main>"u8);
        session.Complete();
        await Assert
            .That(Encoding.UTF8.GetString(output.WrittenSpan))
            .IsEqualTo("<main><a href='x' data-q=\"1\">t</a>finished text<a href='y' data-q=\"1\">t</a></main>");
    }

    [Test]
    public async Task ArbitraryBytesInputIsNormalizedAndRewritten()
    {
        var plan = CreatePlan();
        byte[] source = [.. "<main>"u8, 0xff, .. "<a href='x'>t</a></main>"u8];
        byte[] normalized = [.. "<main>"u8, .. "�"u8, .. "<a href='x'>t</a></main>"u8];
        var expected = RewriteWholeBuffer(plan, normalized, out _);

        foreach (var size in (int[])[1, 3, source.Length])
        {
            var actual = RewriteStreaming(plan, source, Repeat(size), out var matches, Utf8InputContract.ArbitraryBytes);
            await Assert.That(actual.SequenceEqual(expected)).IsTrue();
            await Assert.That(matches).IsEqualTo(1);
        }
    }

    [Test]
    public async Task HoldbackIsBoundedByTheBufferedTokenLimit()
    {
        var plan = CreatePlan();
        var limits = new HtmlStreamingLimits(maximumBufferedTokenBytes: 64);
        var giantTag = $"<div data-filler='{new string('x', 8192)}'>";
        var source = Encoding.UTF8.GetBytes($"<main>{giantTag}<a href=x>t</a></div></main>");

        var output = new ArrayBufferWriter<byte>();
        using var session = plan.CreateRewriteSession(0, output, Edit, Utf8InputContract.WellFormedUtf8, limits);
        var rejected = false;
        try
        {
            for (var offset = 0; offset < source.Length; offset += 32)
                session.Write(source.AsSpan(offset, Math.Min(32, source.Length - offset)));
            session.Complete();
        }
        catch (HtmlStreamingLimitExceededException exception)
        {
            rejected = exception.Limit == HtmlStreamingLimit.BufferedTokenBytes;
        }
        await Assert.That(rejected).IsTrue();
    }

    [Test]
    public async Task UnlimitedLimitsAllowLargeOpenTags()
    {
        var plan = CreatePlan();
        var giantTag = $"<div data-filler='{new string('x', 4 * 1024 * 1024)}'>";
        var source = Encoding.UTF8.GetBytes($"<main>{giantTag}<a href=x>t</a></div></main>");

        var expectedOutput = new ArrayBufferWriter<byte>();
        plan.Rewrite(source, expectedOutput, 0, Edit, Utf8InputContract.WellFormedUtf8, HtmlStreamingLimits.Unlimited);

        var output = new ArrayBufferWriter<byte>();
        using var session = plan.CreateRewriteSession(
            0,
            output,
            Edit,
            Utf8InputContract.WellFormedUtf8,
            HtmlStreamingLimits.Unlimited
        );
        for (var offset = 0; offset < source.Length; offset += 64 * 1024)
            session.Write(source.AsSpan(offset, Math.Min(64 * 1024, source.Length - offset)));
        var matches = session.Complete();

        await Assert.That(output.WrittenSpan.SequenceEqual(expectedOutput.WrittenSpan)).IsTrue();
        await Assert.That(matches).IsEqualTo(1);
    }
}
#endif
