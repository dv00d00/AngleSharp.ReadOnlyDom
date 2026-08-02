using System.Buffers;
using System.Runtime.CompilerServices;

namespace AngleSharp.ReadOnlyDom.Streaming;

/// <summary>
/// Operations for UTF-8 emitted by the tokenizer. Callers must only pass complete,
/// well-formed UTF-8; arbitrary input is validated and repaired at the tokenizer ingress.
/// </summary>
internal static class TrustedUtf8
{
    private static readonly SearchValues<byte> WhiteSpaceCandidates = SearchValues.Create([
        0x09,
        0x0A,
        0x0C,
        0x0D,
        0x20,
        0xC2,
    ]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SequenceLength(byte firstByte) =>
        firstByte < 0xE0 ? 2
        : firstByte < 0xF0 ? 3
        : 4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint DecodeScalar(ReadOnlySpan<byte> utf8, int length)
    {
        var first = utf8[0];
        return length switch
        {
            2 => (uint)(((first & 0x1F) << 6) | (utf8[1] & 0x3F)),
            3 => (uint)(((first & 0x0F) << 12) | ((utf8[1] & 0x3F) << 6) | (utf8[2] & 0x3F)),
            _ => (uint)(((first & 0x07) << 18) | ((utf8[1] & 0x3F) << 12) | ((utf8[2] & 0x3F) << 6) | (utf8[3] & 0x3F)),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsAsciiWhiteSpace(byte value) => value is 0x09 or 0x0A or 0x0C or 0x0D or 0x20;

    internal static int IndexOfWhiteSpace(ReadOnlySpan<byte> utf8, out int byteLength)
    {
        var offset = 0;
        while (offset < utf8.Length)
        {
            var relative = utf8[offset..].IndexOfAny(WhiteSpaceCandidates);
            if (relative < 0)
                break;

            var index = offset + relative;
            var firstByte = utf8[index];
            if (firstByte < 0x80)
            {
                byteLength = 1;
                return index;
            }

            const int scalarLength = 2;
            if (utf8[index + 1] == 0xA0)
            {
                byteLength = scalarLength;
                return index;
            }
            offset = index + scalarLength;
        }

        byteLength = 0;
        return -1;
    }
}
