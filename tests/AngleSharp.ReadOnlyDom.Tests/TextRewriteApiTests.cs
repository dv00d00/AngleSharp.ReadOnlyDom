#if NET10_0
using System.Buffers;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming;
using AngleSharp.ReadOnlyDom.Streaming.Query;
using AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;

namespace AngleSharp.Readonly.Tests;

public sealed class TextRewriteApiTests
{
    [Test]
    public async Task RawEntitiesAndTextTypesAreExposedInsideMatchingDescendants()
    {
        var plan = StreamQuery.For<CaptureState>("div").Compile();
        var state = new CaptureState();
        const string source = "<div>A&amp;B<span>C</span>D<script>x < y</script><textarea>E&amp;F</textarea></div>";
        var output = new ArrayBufferWriter<byte>();

        plan.Rewrite(
            Encoding.UTF8.GetBytes(source),
            output,
            state,
            new HtmlRewriteHandlers<CaptureState>(text: Capture),
            Utf8InputContract.WellFormedUtf8
        );

        await Assert.That(Encoding.UTF8.GetString(output.WrittenSpan)).IsEqualTo(source);
        await Assert.That(string.Concat(state.Raw)).IsEqualTo("A&amp;BCDx < yE&amp;F");
        await Assert
            .That(state.FinalTypes)
            .IsEquivalentTo([
                HtmlTextType.Data,
                HtmlTextType.Data,
                HtmlTextType.Data,
                HtmlTextType.ScriptData,
                HtmlTextType.RcData,
            ]);
    }

    [Test]
    public async Task TextMutationOrderingAndEscapingMatchLolHtml()
    {
        var plan = StreamQuery.For<int>("div").Compile();
        var output = new ArrayBufferWriter<byte>();
        plan.Rewrite(
            "<div>x</div>"u8,
            output,
            0,
            new HtmlRewriteHandlers<int>(text: Edit),
            Utf8InputContract.WellFormedUtf8
        );

        await Assert
            .That(Encoding.UTF8.GetString(output.WrittenSpan))
            .IsEqualTo("<div><b>1</b>&lt;b&gt;2&lt;/b&gt;R&lt;a&gt;2&lt;/a&gt;<a>1</a>!</div>");
    }

    [Test]
    public async Task WholeAndStreamingTextRewritesMatchAcrossEveryByteBoundary()
    {
        var plan = StreamQuery.For<int>("main").Compile();
        const string source =
            "<main>A&amp;B<span>π</span><script>if (a < b) x()</script><textarea>x&amp;y</textarea>Z</main>";
        var handlers = new HtmlRewriteHandlers<int>(text: RemoveAndMarkFinal);
        var expected = Rewrite(plan, source, handlers);

        for (var chunkSize = 1; chunkSize <= Encoding.UTF8.GetByteCount(source); chunkSize++)
            await Assert.That(RewriteStreaming(plan, source, handlers, chunkSize)).IsEqualTo(expected);
    }

    [Test]
    public async Task ElementSuppressionSkipsDescendantTextCallbacks()
    {
        var plan = StreamQuery.For<int>("div").Compile();
        var output = new ArrayBufferWriter<byte>();
        var handlers = new HtmlRewriteHandlers<int>(
            element: static (ref int _, in Element _, ref ElementRewriter element) =>
                element.SetInnerContent("replacement"u8, HtmlRewriteContentType.Text),
            text: static (ref int count, in TextChunk _, ref TextChunkRewriter _) => count++
        );

        var count = plan.Rewrite(
            "<div>discard<span>this</span>too</div>"u8,
            output,
            0,
            handlers,
            Utf8InputContract.WellFormedUtf8
        );

        await Assert.That(count).IsEqualTo(0);
        await Assert.That(Encoding.UTF8.GetString(output.WrittenSpan)).IsEqualTo("<div>replacement</div>");
    }

    [Test]
    public async Task RawTextPlaintextAndCommentsHaveExplicitNodeBoundaries()
    {
        var plan = StreamQuery.For<CaptureState>("main").Compile();
        var state = new CaptureState();
        const string source = "<main>a<!-- split -->b<style>x<y</style><plaintext>p&q</main>";
        var output = new ArrayBufferWriter<byte>();

        plan.Rewrite(
            Encoding.UTF8.GetBytes(source),
            output,
            state,
            new HtmlRewriteHandlers<CaptureState>(text: Capture),
            Utf8InputContract.WellFormedUtf8
        );

        await Assert.That(Encoding.UTF8.GetString(output.WrittenSpan)).IsEqualTo(source);
        await Assert.That(string.Concat(state.Raw)).IsEqualTo("abx<yp&q</main>");
        await Assert
            .That(state.FinalTypes)
            .IsEquivalentTo([HtmlTextType.Data, HtmlTextType.Data, HtmlTextType.RawText, HtmlTextType.PlainText]);
    }

    [Test]
    public async Task ElementAndTextInsertionsComposeAtContentBoundaries()
    {
        var plan = StreamQuery.For<int>("div").Compile();
        var handlers = new HtmlRewriteHandlers<int>(
            element: static (ref int _, in Element _, ref ElementRewriter element) =>
            {
                element.Prepend("P"u8, HtmlRewriteContentType.Text);
                element.Append("A"u8, HtmlRewriteContentType.Text);
            },
            text: static (ref int _, in TextChunk chunk, ref TextChunkRewriter text) =>
            {
                if (chunk.IsLastInTextNode)
                    text.After("!"u8, HtmlRewriteContentType.Text);
                else
                {
                    text.Before("["u8, HtmlRewriteContentType.Text);
                    text.After("]"u8, HtmlRewriteContentType.Text);
                }
            }
        );

        await Assert.That(Rewrite(plan, "<div>x</div>", handlers)).IsEqualTo("<div>P[x]!A</div>");
        await Assert.That(RewriteStreaming(plan, "<div>x</div>", handlers, 1)).IsEqualTo("<div>P[x]!A</div>");
    }

    [Test]
    public async Task LargeTextNodesDoNotRequireSubtreeBuffering()
    {
        var plan = StreamQuery.For<int>("main").Compile();
        var source = $"<main>{new string('x', 256 * 1024)}</main>";
        var handlers = new HtmlRewriteHandlers<int>(text: RemoveAndMarkFinal);
        var input = Encoding.UTF8.GetBytes(source);
        var output = new ArrayBufferWriter<byte>();
        var limits = new HtmlStreamingLimits(maximumBufferedTokenBytes: 64);
        using var session = plan.CreateRewriteSession(0, output, handlers, Utf8InputContract.WellFormedUtf8, limits);
        for (var offset = 0; offset < input.Length; offset += 32)
            session.Write(input.AsSpan(offset, Math.Min(32, input.Length - offset)));
        session.Complete();

        await Assert.That(Encoding.UTF8.GetString(output.WrittenSpan)).IsEqualTo("<main>|</main>");
    }

    [Test]
    public async Task EveryTextModeRemovesExactSourceRangesAcrossChunkBoundaries()
    {
        (string Selector, string Source, string Expected)[] cases =
        [
            ("div", "<div>a&amp;b<3<span>c</span>d<!--x-->e</div>", "<div><span></span><!--x--></div>"),
            ("style", "<style>a</styX>b</style>", "<style></style>"),
            ("textarea", "<textarea>a&amp;b</textareX>c</textarea>", "<textarea></textarea>"),
            ("script", "<script><!--a<script>b</script>tail</script>", "<script></script>"),
            ("plaintext", "<plaintext>a<b>&amp;c", "<plaintext>"),
        ];
        var handlers = new HtmlRewriteHandlers<int>(
            text: static (ref int _, in TextChunk chunk, ref TextChunkRewriter text) =>
            {
                if (!chunk.IsLastInTextNode)
                    text.Remove();
            }
        );

        foreach (var item in cases)
        {
            var plan = StreamQuery.For<int>(item.Selector).Compile();
            await Assert.That(Rewrite(plan, item.Source, handlers)).IsEqualTo(item.Expected);
            for (var chunkSize = 1; chunkSize <= Encoding.UTF8.GetByteCount(item.Source); chunkSize++)
                await Assert.That(RewriteStreaming(plan, item.Source, handlers, chunkSize)).IsEqualTo(item.Expected);
        }
    }

    [Test]
    public async Task RawTextPreservesCrLfAndDoesNotExposeAttributeReferences()
    {
        var query = StreamQuery.For<CaptureState>("span").Attribute("title");
        var plan = query.Compile();
        var state = new CaptureState();
        const string source = "<span title='a&amp;b'>x\r\ny</span>";
        var output = new ArrayBufferWriter<byte>();

        plan.Rewrite(
            Encoding.UTF8.GetBytes(source),
            output,
            state,
            new HtmlRewriteHandlers<CaptureState>(text: Capture),
            Utf8InputContract.WellFormedUtf8
        );

        await Assert.That(string.Concat(state.Raw)).IsEqualTo("x\r\ny");
        await Assert.That(Encoding.UTF8.GetString(output.WrittenSpan)).IsEqualTo(source);
    }

    [Test]
    public async Task TextPayloadEscapingMatchesLolHtmlInEveryTextMode()
    {
        (string Selector, string Source, string Expected)[] cases =
        [
            ("div", "<div>x</div>", "<div>&lt;&amp;&gt;</div>"),
            ("textarea", "<textarea>x</textarea>", "<textarea>&lt;&amp;&gt;</textarea>"),
            ("style", "<style>x</style>", "<style>&lt;&amp;&gt;</style>"),
            ("script", "<script>x</script>", "<script>&lt;&amp;&gt;</script>"),
            ("plaintext", "<plaintext>x", "<plaintext>&lt;&amp;&gt;"),
        ];
        var handlers = new HtmlRewriteHandlers<int>(
            text: static (ref int _, in TextChunk chunk, ref TextChunkRewriter text) =>
            {
                if (!chunk.IsLastInTextNode)
                    text.Replace("<&>"u8, HtmlRewriteContentType.Text);
            }
        );

        foreach (var item in cases)
        {
            var plan = StreamQuery.For<int>(item.Selector).Compile();
            await Assert.That(Rewrite(plan, item.Source, handlers)).IsEqualTo(item.Expected);
        }
    }

    private static void Capture(ref CaptureState state, in TextChunk chunk, ref TextChunkRewriter _)
    {
        if (chunk.IsLastInTextNode)
            state.FinalTypes.Add(chunk.TextType);
        else
            state.Raw.Add(Encoding.UTF8.GetString(chunk.Utf8));
    }

    private static void Edit(ref int _, in TextChunk chunk, ref TextChunkRewriter text)
    {
        if (chunk.IsLastInTextNode)
        {
            text.After("!"u8, HtmlRewriteContentType.Text);
            return;
        }
        text.Before("<b>1</b>"u8, HtmlRewriteContentType.Html);
        text.Before("<b>2</b>"u8, HtmlRewriteContentType.Text);
        text.Replace("R"u8, HtmlRewriteContentType.Text);
        text.After("<a>1</a>"u8, HtmlRewriteContentType.Html);
        text.After("<a>2</a>"u8, HtmlRewriteContentType.Text);
    }

    private static void RemoveAndMarkFinal(ref int _, in TextChunk chunk, ref TextChunkRewriter text)
    {
        if (chunk.IsLastInTextNode)
            text.Before("|"u8, HtmlRewriteContentType.Text);
        else
            text.Remove();
    }

    private static string Rewrite(QueryPlan<int> plan, string source, HtmlRewriteHandlers<int> handlers)
    {
        var output = new ArrayBufferWriter<byte>();
        plan.Rewrite(Encoding.UTF8.GetBytes(source), output, 0, handlers, Utf8InputContract.WellFormedUtf8);
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    private static string RewriteStreaming(
        QueryPlan<int> plan,
        string source,
        HtmlRewriteHandlers<int> handlers,
        int chunkSize
    )
    {
        var input = Encoding.UTF8.GetBytes(source);
        var output = new ArrayBufferWriter<byte>();
        using var session = plan.CreateRewriteSession(0, output, handlers, Utf8InputContract.WellFormedUtf8);
        for (var offset = 0; offset < input.Length; offset += chunkSize)
            session.Write(input.AsSpan(offset, Math.Min(chunkSize, input.Length - offset)));
        session.Complete();
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    private sealed class CaptureState
    {
        internal List<string> Raw { get; } = [];
        internal List<HtmlTextType> FinalTypes { get; } = [];
    }
}
#endif
