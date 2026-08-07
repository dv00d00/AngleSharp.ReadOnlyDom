#if NET10_0
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;

namespace AngleSharp.ReadOnlyDom.Benchmarks.Support;

/// <summary>
/// The tokenizer hands sinks raw (undecoded) attribute values; differential sinks that compare
/// against AngleSharp's decoded output run the production decoder eagerly through this helper.
/// </summary>
internal static class AttributeValueDecoding
{
    public static byte[] Decode(ReadOnlySpan<byte> value, bool valueMayContainReferences)
    {
        var ampersand = valueMayContainReferences ? value.IndexOf((byte)'&') : -1;
        if (ampersand < 0)
        {
            return value.ToArray();
        }
        var buffer = new Utf8TokenBuffer(value.Length + 8);
        Utf8AttributeValueDecoder.Decode(value, ampersand, buffer);
        return buffer.WrittenSpan.ToArray();
    }
}
#endif
