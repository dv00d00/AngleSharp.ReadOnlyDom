using System.Runtime.CompilerServices;

namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

#pragma warning disable CS1591 // Experimental API surface; shape is intentionally unsettled.

internal static class Utf8NameHash
{
    public const UInt64 Offset = 14695981039346656037;
    private const UInt64 Prime = 1099511628211;

    internal static UInt64 Append(UInt64 hash, Byte value) => (hash ^ value) * Prime;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Byte ToLowerAscii(Byte value) =>
        (UInt32)(value - (Byte)'A') <= 'Z' - 'A' ? (Byte)(value | 0x20) : value;

    internal static UInt64 Append(UInt64 hash, ReadOnlySpan<Byte> value)
    {
        foreach (var character in value)
        {
            hash = (hash ^ character) * Prime;
        }

        return hash;
    }

    internal static UInt64 Compute(ReadOnlySpan<Byte> value) => Append(Offset, value);

    public static UInt64 ComputeSemantic(ReadOnlySpan<Byte> value)
    {
        var hash = Offset;
        foreach (var character in value)
        {
            hash = Append(hash, ToLowerAscii(character));
        }

        return hash;
    }

    /// <summary>
    /// Maps a semantic name hash to its single bit in the 64-bit attribute-name pre-filter
    /// (see <see cref="IUtf8HtmlTokenSink.StartTagAttributeFilter"/>). The low hash bits are
    /// used: every input byte reaches them through an odd multiplier, whereas FNV-1a's high
    /// bits barely avalanche for short inputs (measured: all three-letter names sharing a
    /// first letter land on one top-six-bit value).
    /// </summary>
    internal static UInt64 AttributeFilterBit(UInt64 semanticHash) => 1UL << (Int32)(semanticHash & 63);

    internal static UInt64 ComputeSemanticWithUppercasePrescan(ReadOnlySpan<Byte> value)
    {
        if (value.IndexOfAnyInRange((Byte)'A', (Byte)'Z') < 0)
        {
            return Compute(value);
        }

        return ComputeSemantic(value);
    }
}
