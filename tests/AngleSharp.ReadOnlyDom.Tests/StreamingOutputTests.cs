#if NET10_0
using System.Buffers;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming;
using AngleSharp.ReadOnlyDom.Streaming.Internal;
using AngleSharp.ReadOnlyDom.Streaming.Public;

namespace AngleSharp.Readonly.Tests;

public sealed class StreamingOutputTests
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

        writer.Append(Encoding.UTF8.GetBytes("  Alpha\u00a0\u2003"));
        writer.ParagraphBreak();
        writer.Append("Beta Ж🙂"u8);
        writer.CellBreak();
        writer.Append("Gamma"u8);
        writer.LineBreak();
        writer.Append("Delta  "u8);
        writer.ParagraphBreak();

        await Assert
            .That(Encoding.UTF8.GetString(output.WrittenSpan))
            .IsEqualTo("Alpha \u2003\n\nBeta Ж🙂\tGamma\nDelta");
    }

    [Test]
    public async Task TrustedWhitespaceClassifierMatchesNormalizationContractForEveryScalar()
    {
        var mismatch = -1;
        Span<byte> encoded = stackalloc byte[4];
        for (var scalar = 0; scalar <= 0x10FFFF; scalar++)
        {
            if (scalar is >= 0xD800 and <= 0xDFFF)
                continue;

            var length = new Rune(scalar).EncodeToUtf8(encoded);
            var expected = scalar is 0x09 or 0x0A or 0x0C or 0x0D or 0x20 or 0x00A0;
            var actual =
                TrustedUtf8.IndexOfWhiteSpace(encoded[..length], out var whitespaceLength) == 0
                && whitespaceLength == length;
            if (actual == expected)
                continue;

            mismatch = scalar;
            break;
        }

        await Assert.That(mismatch).IsEqualTo(-1);
    }
}
#endif
