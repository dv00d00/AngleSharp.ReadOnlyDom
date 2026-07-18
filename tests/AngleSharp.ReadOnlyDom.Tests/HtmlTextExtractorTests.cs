#if NET10_0
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming;

namespace AngleSharp.Readonly.Tests;

public sealed class HtmlTextExtractorTests
{
    [Test]
    public async Task PublishableBufferRetainsTentativeSuffixWhenPublishedPrefixAdvances()
    {
        var buffer = new PublishableUtf8Buffer(4);
        buffer.Write("abc"u8);
        buffer.MarkPublishable();
        buffer.Write("defgh"u8);

        await Assert.That(Encoding.UTF8.GetString(buffer.PublishableUtf8.Span)).IsEqualTo("abc");
        buffer.AdvancePublished(3);
        await Assert.That(Encoding.UTF8.GetString(buffer.WrittenUtf8.Span)).IsEqualTo("defgh");

        buffer.MarkPublishable();
        buffer.AdvancePublished(2);
        await Assert.That(Encoding.UTF8.GetString(buffer.PublishableUtf8.Span)).IsEqualTo("fgh");
    }

    [Test]
    public async Task NormalizedWriterCollapsesUnicodeWhitespaceAndDelaysSeparators()
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new NormalizedUtf8Writer(output);

        writer.Append(Encoding.UTF8.GetBytes("  Alpha\u00a0"));
        writer.ParagraphBreak();
        writer.Append("Beta"u8);
        writer.CellBreak();
        writer.Append("Gamma"u8);
        writer.LineBreak();
        writer.Append("Delta  "u8);
        writer.ParagraphBreak();

        await Assert.That(Encoding.UTF8.GetString(output.WrittenSpan)).IsEqualTo("Alpha\n\nBeta\tGamma\nDelta");
    }

    [Test]
    public async Task DefaultExtractorProducesSemanticBodyTextWithoutScriptOrHeadContent()
    {
        const string html = """
            <html><head><title>Hidden</title><img alt="also hidden"></head><body>before<script>bad()</script>
            <h1>Hello&nbsp;world</h1><p>One <b>useful</b><br>line.</p><img alt="Chart">
            <table><tr><td>A</td><td>B</td></tr></table></body></html>
            """;

        var text = HtmlTextExtractor.Default.Extract(Encoding.UTF8.GetBytes(html));

        await Assert.That(text).IsEqualTo("before\n\nHello world\n\nOne useful\nline.\n\nChart\n\nA\tB");
    }

    [Test]
    public async Task OptionsCustomizeRootElementsSeparatorsAndImageText()
    {
        var extractor = new HtmlTextExtractor(
            new HtmlTextOptions
            {
                ContentElement = "main",
                IgnoredElements = ["span"],
                BlockElements = ["p"],
                LineBreakElements = ["br"],
                CellElements = ["td"],
                LineSeparator = ";",
                ParagraphSeparator = "||",
                CellSeparator = "~",
                IncludeImageAltText = false,
            }
        );
        const string html =
            "<html><body>outside<main>x<span>skip</span><p>one<br>two</p>"
            + "<img alt=gone><table><tr><td>A</td><td>B</td></tr></table></main></body></html>";

        var text = extractor.Extract(Encoding.UTF8.GetBytes(html));

        await Assert.That(text).IsEqualTo("x||one;two||A~B");
    }

    [Test]
    public async Task BufferedAndBackpressuredExtractionProduceIdenticalBytes()
    {
        var html = Encoding.UTF8.GetBytes(
            "<html><body><h1>Hello</h1><p>From <b>streaming</b>.</p><table><tr><td>A</td><td>B</td></tr></table></body></html>"
        );
        var buffered = HtmlTextExtractor.Default.ExtractUtf8(html);
        await using var inputStream = new MemoryStream(html);
        await using var outputStream = new MemoryStream();
        var reader = PipeReader.Create(inputStream, new StreamPipeReaderOptions(leaveOpen: true));
        var writer = PipeWriter.Create(outputStream, new StreamPipeWriterOptions(leaveOpen: true));

        await HtmlTextExtractor.Default.ExtractAsync(reader, writer, flushThreshold: 8, inputSliceSize: 3);
        await reader.CompleteAsync();
        await writer.CompleteAsync();

        await Assert.That(outputStream.ToArray().SequenceEqual(buffered)).IsTrue();
    }
}
#endif
