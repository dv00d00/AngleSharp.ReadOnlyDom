namespace AngleSharp.ReadOnlyDom.Streaming;

internal static class Utf8NameHash
{
    internal const ulong Offset = 14695981039346656037;
    private const ulong Prime = 1099511628211;

    internal static ulong Append(ulong hash, byte value) => (hash ^ value) * Prime;

    internal static ulong Append(ulong hash, ReadOnlySpan<byte> value)
    {
        foreach (var character in value)
            hash = Append(hash, character);
        return hash;
    }

    internal static ulong Compute(ReadOnlySpan<byte> value) => Append(Offset, value);
}
