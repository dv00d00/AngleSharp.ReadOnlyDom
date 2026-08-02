using System.Runtime.InteropServices;
using AngleSharp.Common;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal sealed partial class Arena
{
    private char[] OwnTextValues()
    {
        var payloadCount = _payloads?.Count ?? 0;
        var attributeCount = _attributes?.Count ?? 0;

        // Values are slices of the tokenizer's append-only char buffer (one array per parse) or
        // interned strings. Copying each backing array's used range once and rebasing the slices
        // with offset arithmetic is much cheaper than a per-value copy; strings need no copy at all.
        var regions = new ValueRegion[4];
        var regionCount = 0;
        var bulk = true;
        for (var payload = 0; payload < payloadCount && bulk; payload++)
            bulk = TrackValueRegion(_payloads![payload].Value, regions, ref regionCount);
        for (var attribute = 0; attribute < attributeCount && bulk; attribute++)
            bulk = TrackValueRegion(_attributes![attribute].Value, regions, ref regionCount);

        if (bulk)
        {
            var total = 0L;
            for (var region = 0; region < regionCount; region++)
                total += regions[region].End - regions[region].Start;
            // Gaps between retained values (filtered attributes, dropped whitespace) inflate the
            // range; fall back to the dense copy when the overhead outgrows the retained text.
            if (total <= Math.Max((long)_textLength * 2, 4096))
            {
                var text = Allocate<char>((int)Math.Max(total, 1));
                var position = 0;
                for (var region = 0; region < regionCount; region++)
                {
                    ref var current = ref regions[region];
                    current.DestinationStart = position;
                    current.Array.AsSpan(current.Start, current.End - current.Start).CopyTo(text.AsSpan(position));
                    position += current.End - current.Start;
                }

                for (var payload = 0; payload < payloadCount; payload++)
                    RebaseValue(ref _payloads![payload].Value, regions, regionCount, text);
                for (var attribute = 0; attribute < attributeCount; attribute++)
                    RebaseValue(ref _attributes![attribute].Value, regions, regionCount, text);
                return text;
            }
        }

        var dense = Allocate<char>(_textLength);
        var densePosition = 0;
        for (var payload = 0; payload < payloadCount; payload++)
            OwnTextValue(ref _payloads![payload].Value, dense, ref densePosition);
        for (var attribute = 0; attribute < attributeCount; attribute++)
            OwnTextValue(ref _attributes![attribute].Value, dense, ref densePosition);
        return dense;
    }

    /// <summary>Extends the backing-array regions with one value; false forces the dense fallback.</summary>
    private static bool TrackValueRegion(in StringOrMemory value, ValueRegion[] regions, ref int regionCount)
    {
        if (value.Length == 0)
            return true;
        if (!MemoryMarshal.TryGetArray(value.Memory, out var segment))
            return MemoryMarshal.TryGetString(value.Memory, out _, out _, out _);
        for (var region = 0; region < regionCount; region++)
        {
            ref var current = ref regions[region];
            if (!ReferenceEquals(current.Array, segment.Array))
                continue;
            current.Start = Math.Min(current.Start, segment.Offset);
            current.End = Math.Max(current.End, segment.Offset + segment.Count);
            return true;
        }

        if (regionCount == regions.Length)
            return false;
        regions[regionCount++] = new ValueRegion
        {
            Array = segment.Array!,
            Start = segment.Offset,
            End = segment.Offset + segment.Count
        };
        return true;
    }

    private static void RebaseValue(ref StringOrMemory value, ValueRegion[] regions, int regionCount, char[] text)
    {
        if (value.Length == 0 || !MemoryMarshal.TryGetArray(value.Memory, out var segment))
            return;
        for (var region = 0; region < regionCount; region++)
        {
            ref var current = ref regions[region];
            if (!ReferenceEquals(current.Array, segment.Array))
                continue;
            value = new StringOrMemory(
                text.AsMemory(current.DestinationStart + segment.Offset - current.Start, segment.Count)
            );
            return;
        }
    }

    private static void OwnTextValue(ref StringOrMemory value, char[] destination, ref int position)
    {
        if (value.Length == 0)
            return;
        value.Memory.Span.CopyTo(destination.AsSpan(position));
        value = new StringOrMemory(destination.AsMemory(position, value.Length));
        position += value.Length;
    }

    private static (int Start, int Length) CopyText(StringOrMemory value, PooledValueBuffer<char> destination)
    {
        if (value.Length == 0)
            return (-1, 0);
        var start = destination.Count;
        destination.AddRange(value.Memory.Span);
        return (start, value.Length);
    }

    private struct ValueRegion
    {
        public char[] Array;
        public int Start;
        public int End;
        public int DestinationStart;
    }
}