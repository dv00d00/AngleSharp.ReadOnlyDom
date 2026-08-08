#if NET10_0
using System.Buffers;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming;
using AngleSharp.ReadOnlyDom.Streaming.Query;
using AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;

namespace AngleSharp.Readonly.Tests;

public sealed class HtmlRewriteApiTests
{
    [Test]
    public async Task AttributesCanBeSetRemovedAndAppended()
    {
        var plan = StreamQuery.For<int>("div").Compile();
        var actual = Rewrite(
            plan,
            "<div ID='a' class=x data-old='1'></div>",
            static (ref int _, in Element _, ref ElementRewriter element) =>
            {
                element.SetAttribute("id"u8, "b"u8);
                element.RemoveAttribute("CLASS"u8);
                element.SetAttribute("title"u8, "a&\"b"u8);
                element.AppendAttribute("data-z"u8, "1"u8);
            }
        );

        await Assert.That(actual).IsEqualTo("<div id=\"b\" data-old='1' title=\"a&amp;&quot;b\" data-z=\"1\"></div>");
    }

    [Test]
    public async Task InsertionsMatchLolHtmlOrderingAndEscapeText()
    {
        var plan = StreamQuery.For<int>("div").Compile();
        var actual = Rewrite(
            plan,
            "<div>x</div>",
            static (ref int _, in Element _, ref ElementRewriter element) =>
            {
                element.Before("<b>1</b>"u8, HtmlRewriteContentType.Html);
                element.Before("<b>2</b>"u8, HtmlRewriteContentType.Text);
                element.Prepend("A"u8, HtmlRewriteContentType.Text);
                element.Prepend("B"u8, HtmlRewriteContentType.Text);
                element.Append("C"u8, HtmlRewriteContentType.Text);
                element.Append("D"u8, HtmlRewriteContentType.Text);
                element.After("<i>1</i>"u8, HtmlRewriteContentType.Html);
                element.After("<i>2</i>"u8, HtmlRewriteContentType.Text);
            }
        );

        await Assert.That(actual).IsEqualTo("<b>1</b>&lt;b&gt;2&lt;/b&gt;<div>BAxCD</div>&lt;i&gt;2&lt;/i&gt;<i>1</i>");
    }

    [Test]
    public async Task SetInnerContentSuppressesOriginalDescendantsWithoutBufferingThem()
    {
        var plan = StreamQuery.For<int>("div").Compile();
        var source = $"<div><span>{new string('x', 16 * 1024)}</span></div>";
        var expected = "<div><b>before</b>&lt;replacement&amp;&gt;<b>after</b></div>";
        RewriteHandler<int> handler = static (ref int _, in Element _, ref ElementRewriter element) =>
        {
            element.Prepend("discarded"u8, HtmlRewriteContentType.Text);
            element.Append("discarded"u8, HtmlRewriteContentType.Text);
            element.SetInnerContent("<replacement&>"u8, HtmlRewriteContentType.Text);
            element.Prepend("<b>before</b>"u8, HtmlRewriteContentType.Html);
            element.Append("<b>after</b>"u8, HtmlRewriteContentType.Html);
        };

        await Assert.That(Rewrite(plan, source, handler)).IsEqualTo(expected);
        await Assert
            .That(RewriteStreaming(plan, source, handler, 7, maximumBufferedTokenBytes: 64))
            .IsEqualTo(expected);
    }

    [Test]
    public async Task ReplaceRemoveAndUnwrapOperateOnWholeElements()
    {
        var replace = StreamQuery.For<int>("section").Compile();
        var replaced = Rewrite(
            replace,
            "<main><section><b>old</b></section><p>tail</p></main>",
            static (ref int _, in Element _, ref ElementRewriter element) =>
            {
                element.Before("["u8, HtmlRewriteContentType.Text);
                element.Replace("<article>new</article>"u8, HtmlRewriteContentType.Html);
                element.After("]"u8, HtmlRewriteContentType.Text);
            }
        );
        await Assert.That(replaced).IsEqualTo("<main>[<article>new</article>]<p>tail</p></main>");

        var unclosed = StreamQuery.For<int>("section").Compile();
        await Assert
            .That(
                Rewrite(
                    unclosed,
                    "<section>unterminated",
                    static (ref int _, in Element _, ref ElementRewriter element) =>
                    {
                        element.Replace("replacement"u8, HtmlRewriteContentType.Text);
                        element.After("not-emitted"u8, HtmlRewriteContentType.Text);
                    }
                )
            )
            .IsEqualTo("replacement");

        var remove = StreamQuery.For<int>("aside").Compile();
        await Assert
            .That(
                Rewrite(
                    remove,
                    "<main>a<aside><b>noise</b></aside>z</main>",
                    static (ref int _, in Element _, ref ElementRewriter element) => element.Remove()
                )
            )
            .IsEqualTo("<main>az</main>");

        var unwrap = StreamQuery.For<int>("span").Compile();
        await Assert
            .That(
                Rewrite(
                    unwrap,
                    "<p>a<span class=x><b>kept</b></span>z</p>",
                    static (ref int _, in Element _, ref ElementRewriter element) => element.RemoveAndKeepContent()
                )
            )
            .IsEqualTo("<p>a<b>kept</b>z</p>");
    }

    [Test]
    public async Task NonVoidSelfClosingSyntaxIsOpenedForInsertedContentAndVoidContentIsIgnored()
    {
        var div = StreamQuery.For<int>("div").Compile();
        await Assert
            .That(
                Rewrite(
                    div,
                    "<div/>x</div>",
                    static (ref int _, in Element _, ref ElementRewriter element) =>
                    {
                        element.Prepend("P"u8, HtmlRewriteContentType.Text);
                        element.Append("A"u8, HtmlRewriteContentType.Text);
                    }
                )
            )
            .IsEqualTo("<div>PxA</div>");

        var image = StreamQuery.For<int>("img").Compile();
        await Assert
            .That(
                Rewrite(
                    image,
                    "x<img src=x>y",
                    static (ref int _, in Element _, ref ElementRewriter element) =>
                    {
                        element.Before("B"u8, HtmlRewriteContentType.Text);
                        element.Prepend("ignored"u8, HtmlRewriteContentType.Text);
                        element.Append("ignored"u8, HtmlRewriteContentType.Text);
                        element.After("A"u8, HtmlRewriteContentType.Text);
                    }
                )
            )
            .IsEqualTo("xB<img src=x>Ay");
    }

    [Test]
    public async Task WholeBufferAndStreamingResultsMatchAcrossEveryByteBoundary()
    {
        var plan = StreamQuery.For<int>("article").Compile();
        const string source = "<main><article class='old'><p>body</p></article><article>x</article></main>";
        RewriteHandler<int> handler = static (ref int _, in Element _, ref ElementRewriter element) =>
        {
            element.SetAttribute("class"u8, "new"u8);
            element.Prepend("<header>h</header>"u8, HtmlRewriteContentType.Html);
            element.Append("<footer>f</footer>"u8, HtmlRewriteContentType.Html);
        };
        var expected = Rewrite(plan, source, handler);
        for (var chunkSize = 1; chunkSize <= Encoding.UTF8.GetByteCount(source); chunkSize++)
            await Assert.That(RewriteStreaming(plan, source, handler, chunkSize)).IsEqualTo(expected);
    }

    [Test]
    public async Task RawTextEndTagsRemainEditableAcrossChunkBoundaries()
    {
        var plan = StreamQuery.For<int>("script").Compile();
        const string source = "<script>if (a < b) x()</script><p>tail</p>";
        RewriteHandler<int> handler = static (ref int _, in Element _, ref ElementRewriter element) =>
            element.Append("/*done*/"u8, HtmlRewriteContentType.Text);
        const string expected = "<script>if (a < b) x()/*done*/</script><p>tail</p>";

        await Assert.That(Rewrite(plan, source, handler)).IsEqualTo(expected);
        for (var chunkSize = 1; chunkSize <= 16; chunkSize++)
            await Assert.That(RewriteStreaming(plan, source, handler, chunkSize)).IsEqualTo(expected);
    }

    private static string Rewrite(QueryPlan<int> plan, string source, RewriteHandler<int> handler)
    {
        var output = new ArrayBufferWriter<byte>();
        plan.Rewrite(Encoding.UTF8.GetBytes(source), output, 0, handler, Utf8InputContract.WellFormedUtf8);
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    private static string RewriteStreaming(
        QueryPlan<int> plan,
        string source,
        RewriteHandler<int> handler,
        int chunkSize,
        int maximumBufferedTokenBytes = 1024 * 1024
    )
    {
        var input = Encoding.UTF8.GetBytes(source);
        var output = new ArrayBufferWriter<byte>();
        var limits = new HtmlStreamingLimits(maximumBufferedTokenBytes: maximumBufferedTokenBytes);
        using var session = plan.CreateRewriteSession(0, output, handler, Utf8InputContract.WellFormedUtf8, limits);
        for (var offset = 0; offset < input.Length; offset += chunkSize)
            session.Write(input.AsSpan(offset, Math.Min(chunkSize, input.Length - offset)));
        session.Complete();
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }
}
#endif
