using System.Text;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.Streaming.AngleSharp;

namespace AngleSharp.ReadOnlyDom.Streaming.AngleSharp.Tests;

public sealed class Utf8AngleSharpTokenSourceTests
{
    [Test]
    public async Task SegmentedUtf8FeedsPublicAngleSharpTreeBuilderSeam()
    {
        const string html = "<!doctype html><main data-id='42'>hé &amp; <b>bold</b></main>";
        using var source = new Utf8HtmlTokenSource(Segments(html, 3));

        var document = await new HtmlParser().ParseDocumentAsync(source);

        await Assert.That(document.QuerySelector("main")?.GetAttribute("data-id")).IsEqualTo("42");
        await Assert.That(document.QuerySelector("main")?.TextContent).IsEqualTo("hé & bold");
    }

    [Test]
    public async Task TreeBuilderControlsTokenizerModeBetweenTokens()
    {
        const string html = "<svg><title><b>x</b></title></svg><textarea>a&amp;b</textarea>";
        using var source = new Utf8HtmlTokenSource(Segments(html, 2));

        var document = await new HtmlParser().ParseDocumentAsync(source);

        await Assert.That(document.QuerySelector("svg title")?.FirstElementChild?.LocalName).IsEqualTo("b");
        await Assert.That(document.QuerySelector("textarea")?.TextContent).IsEqualTo("a&b");
    }

    [Test]
    public async Task InvalidUtf8BecomesReplacementText()
    {
        var prefix = Encoding.UTF8.GetBytes("<main>a");
        var suffix = Encoding.UTF8.GetBytes("b</main>");
        var bytes = new byte[prefix.Length + 1 + suffix.Length];
        prefix.CopyTo(bytes, 0);
        bytes[prefix.Length] = 0x80;
        suffix.CopyTo(bytes, prefix.Length + 1);
        using var source = new Utf8HtmlTokenSource(Segments(bytes, 1));

        var document = await new HtmlParser().ParseDocumentAsync(source);

        await Assert.That(document.QuerySelector("main")?.TextContent).IsEqualTo("a\uFFFDb");
    }

    private static IAsyncEnumerable<ReadOnlyMemory<byte>> Segments(string html, int segmentSize) =>
        Segments(Encoding.UTF8.GetBytes(html), segmentSize);

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Segments(byte[] bytes, int segmentSize)
    {
        for (var offset = 0; offset < bytes.Length; offset += segmentSize)
        {
            await Task.Yield();
            yield return bytes.AsMemory(offset, Math.Min(segmentSize, bytes.Length - offset));
        }
    }
}
