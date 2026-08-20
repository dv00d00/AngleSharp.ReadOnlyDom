using System.Runtime.InteropServices;

namespace AngleSharp.ReadOnlyDom.Compact.Document;

public sealed partial class CompactDocument
{
    /// <summary>
    ///     Returns the next node at or after <paramref name="start" /> with the given name ID, or -1.
    ///     Frozen columns use a vectorized scan; packed documents use a scalar scan.
    /// </summary>
    internal int IndexOfName(string name, int start = 0, int endExclusive = int.MaxValue)
    {
        return IndexOfName(name.AsSpan(), start, endExclusive);
    }

    /// <summary>
    ///     Resolves <paramref name="name" /> once, then returns the next matching node at or after
    ///     <paramref name="start" />, or -1.
    /// </summary>
    internal int IndexOfName(ReadOnlySpan<char> name, int start = 0, int endExclusive = int.MaxValue)
    {
        return IndexOfName(ResolveNameId(name), start, endExclusive);
    }

    /// <summary>
    ///     Returns the next node at or after <paramref name="start" /> with the given pre-resolved name ID, or -1.
    /// </summary>
    internal int IndexOfName(ushort nameId, int start = 0, int endExclusive = int.MaxValue)
    {
        if (start < 0)
            start = 0;
        endExclusive = Math.Min(endExclusive, _nodeCount);
        if (start >= endExclusive)
            return -1;
        if (_arena is not null)
        {
            var column = _arena.NameIdColumn;
            var relative = MemoryMarshal
                .Cast<ushort, char>(column.Slice(start, endExclusive - start))
                .IndexOf((char)nameId);
            return relative < 0 ? -1 : start + relative;
        }

        for (var handle = start; handle < endExclusive; handle++)
            if (_nodes![handle].NameId == nameId)
                return handle;
        return -1;
    }

    internal int CountElements(string name)
    {
        return CountElements(name.AsSpan());
    }

    /// <summary>Resolves <paramref name="name" /> once, then counts matching elements.</summary>
    internal int CountElements(ReadOnlySpan<char> name)
    {
        return CountElements(ResolveNameId(name));
    }

    /// <summary>Counts elements using a previously resolved name ID.</summary>
    internal int CountElements(ushort nameId)
    {
        var count = 0;
        for (var handle = 0; handle < _nodeCount; handle++)
        {
            if (TryGetContainingTemplateContentEnd(handle, out var contentEnd))
            {
                handle = contentEnd - 1;
                continue;
            }

            if (KindAt(handle) == CompactNodeKind.Element && NameIdAt(handle) == nameId)
                count++;
        }

        return count;
    }

    internal string GetName(ushort id)
    {
        return id < GeneratedTagMetadata.KnownNameCount
            ? GeneratedTagMetadata.GetKnownName(id)
            : _names[id - GeneratedTagMetadata.KnownNameCount];
    }

    /// <summary>
    ///     Resolves a name only when it occurs in this document. This scans nodes and attributes.
    /// </summary>
    internal ushort FindNameId(string name)
    {
        return FindNameId(name.AsSpan());
    }

    internal ushort FindNameId(ReadOnlySpan<char> name)
    {
        var id = ResolveNameId(name);
        return id != ushort.MaxValue && ContainsNameId(id) ? id : ushort.MaxValue;
    }

    /// <summary>
    ///     Resolves a stable name ID without checking whether it occurs in this document.
    /// </summary>
    internal ushort ResolveNameId(string name)
    {
        return ResolveNameId(name.AsSpan());
    }

    internal ushort ResolveNameId(ReadOnlySpan<char> name)
    {
        if (GeneratedTagMetadata.TryGetKnownNameId(name, out var knownId))
            return knownId;
        for (ushort i = 0; i < _nameCount; i++)
            if (name.SequenceEqual(_names[i]))
                return checked((ushort)(GeneratedTagMetadata.KnownNameCount + i));
        return ushort.MaxValue;
    }

    private bool ContainsNameId(ushort id)
    {
        for (var handle = 0; handle < _nodeCount; handle++)
            if (NameIdAt(handle) == id)
                return true;
        for (var attribute = 0; attribute < _attributeCount; attribute++)
            if (AttributeNameIdAt(attribute) == id)
                return true;
        return false;
    }
}
