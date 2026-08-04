using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AngleSharp.Text;
using ArenaStorage = AngleSharp.ReadOnlyDom.Compact.Arena.Arena;

namespace AngleSharp.ReadOnlyDom.Compact.Document;

public sealed class CompactDocument : IDisposable
{
    private readonly ArenaStorage? _arena;
    private readonly CompactAttribute[]? _attributes;
    private readonly bool _hasTemplates;
    private readonly CompactMetadataOptions _metadataOptions;
    private readonly int _nameCount;
    private readonly int _nodeCount;
    private readonly int _attributeCount;

    private readonly string[] _names;
    private readonly CompactNode[]? _nodes;
    private readonly int[]? _parents;
    private readonly CompactNodePayload[]? _payloads;
    private readonly TextSource? _source;
    private readonly CompactSourceLocation[]? _sources;
    private readonly CompactTemplateBoundary[] _templateBoundaries;
    private readonly char[]? _text;
    private int _disposed;

    internal CompactDocument(
        CompactNode[] nodes,
        CompactNodePayload[] payloads,
        CompactAttribute[] attributes,
        string[] names,
        char[] text,
        int[]? parents,
        CompactSourceLocation[]? sources,
        CompactTemplateBoundary[] templateBoundaries,
        int nodeCount,
        int payloadCount,
        int attributeCount,
        int nameCount,
        int textLength
    )
    {
        _nodes = nodes;
        _payloads = payloads;
        _attributes = attributes;
        _names = names;
        _text = text;
        _parents = parents;
        _sources = sources;
        _templateBoundaries = templateBoundaries;
        _nodeCount = nodeCount;
        PayloadCount = payloadCount;
        _attributeCount = attributeCount;
        _nameCount = nameCount;
        TextLength = textLength;
        _hasTemplates = templateBoundaries.Length != 0;
    }

    internal CompactDocument(
        ArenaStorage arena,
        TextSource source,
        string[] names,
        int nodeCount,
        int payloadCount,
        int attributeCount,
        int nameCount,
        int textLength,
        CompactMetadataOptions metadataOptions,
        CompactTemplateBoundary[] templateBoundaries,
        char[] text
    )
    {
        _arena = arena;
        _source = source;
        _names = names;
        _nodeCount = nodeCount;
        PayloadCount = payloadCount;
        _attributeCount = attributeCount;
        _nameCount = nameCount;
        TextLength = textLength;
        _metadataOptions = metadataOptions;
        _templateBoundaries = templateBoundaries;
        _text = text;
        _hasTemplates = templateBoundaries.Length != 0;
    }

    internal CompactDocumentLayout Layout =>
        _arena is null ? CompactDocumentLayout.Packed : CompactDocumentLayout.FrozenColumns;

    public int NodeCount
    {
        get
        {
            ThrowIfDisposed();
            return _nodeCount;
        }
    }

    public int AttributeCount
    {
        get
        {
            ThrowIfDisposed();
            return _attributeCount;
        }
    }

    internal int RawNodeCount => _nodeCount;

    internal int PayloadCount { get; }

    internal int TextLength { get; }

    public bool HasParentLinks
    {
        get
        {
            ThrowIfDisposed();
            return RetainsParentLinks;
        }
    }

    public bool HasSourceLocations
    {
        get
        {
            ThrowIfDisposed();
            return RetainsSourceLocations;
        }
    }

    private bool RetainsParentLinks =>
        _arena is null ? _parents is not null : _metadataOptions.HasFlag(CompactMetadataOptions.ParentLinks);

    private bool RetainsSourceLocations =>
        _arena is null ? _sources is not null : _metadataOptions.HasFlag(CompactMetadataOptions.SourceLocations);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(CompactDocument));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_names.Length != 0)
            ArrayPool<string>.Shared.Return(_names, true);
        if (_arena is not null)
        {
            try
            {
                _arena.Dispose();
            }
            finally
            {
                try
                {
                    _source!.Dispose();
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(_text!);
                }
            }

            return;
        }

        ArrayPool<CompactNode>.Shared.Return(_nodes!);
        ArrayPool<CompactNodePayload>.Shared.Return(_payloads!);
        ArrayPool<CompactAttribute>.Shared.Return(_attributes!);
        ArrayPool<char>.Shared.Return(_text!);
        if (_parents is not null)
            ArrayPool<int>.Shared.Return(_parents);
        if (_sources is not null)
            ArrayPool<CompactSourceLocation>.Shared.Return(_sources);
    }

    internal CompactNode GetNode(int handle)
    {
        if (_arena is null)
            return _nodes![handle];
        return new CompactNode(
            _arena.FrozenFirstChild(handle),
            _arena.FrozenSubtreeEnd(handle),
            _arena.FrozenPayloadIndex(handle),
            _arena.FrozenNameId(handle),
            _arena.FrozenKind(handle),
            _arena.FrozenFlags(handle)
        );
    }

    internal CompactNodeKind KindAt(int handle)
    {
        return _arena is null ? _nodes![handle].Kind : _arena.FrozenKind(handle);
    }

    internal ushort NameIdAt(int handle)
    {
        return _arena is null ? _nodes![handle].NameId : _arena.FrozenNameId(handle);
    }

    internal int PayloadIndexAt(int handle)
    {
        return _arena is null ? _nodes![handle].PayloadIndex : _arena.FrozenPayloadIndex(handle);
    }

    internal int SubtreeEndAt(int handle)
    {
        return _arena is null ? _nodes![handle].SubtreeEndExclusive : _arena.FrozenSubtreeEnd(handle);
    }

    internal CompactNodePayload GetPayload(int index)
    {
        if (_arena is null)
            return _payloads![index];
        var value = _arena.FrozenPayloadValue(index);
        return new CompactNodePayload(
            _arena.FrozenFirstAttribute(index),
            value.IsEmpty ? -1 : EncodePayloadValue(index),
            value.Length,
            _arena.FrozenAttributeCount(index)
        );
    }

    internal CompactAttribute GetAttribute(int index)
    {
        if (_arena is null)
            return _attributes![index];
        var value = _arena.FrozenAttributeValue(index);
        return new CompactAttribute(
            _arena.FrozenAttributeNameId(index),
            value.IsEmpty ? -1 : EncodeAttributeValue(index),
            value.Length
        );
    }

    // Lightweight column accessors used by the hot attribute-lookup loops. They avoid materializing a
    // CompactNode/CompactNodePayload/CompactAttribute struct (and, on the frozen path, the encode/decode
    // round-trip) when the caller only needs the name ID or the value span.
    internal int PayloadFirstAttributeAt(int payloadIndex)
    {
        return _arena is null ? _payloads![payloadIndex].FirstAttribute : _arena.FrozenFirstAttribute(payloadIndex);
    }

    internal int PayloadAttributeCountAt(int payloadIndex)
    {
        return _arena is null ? _payloads![payloadIndex].AttributeCount : _arena.FrozenAttributeCount(payloadIndex);
    }

    internal ushort AttributeNameIdAt(int index)
    {
        return _arena is null ? _attributes![index].NameId : _arena.FrozenAttributeNameId(index);
    }

    internal ReadOnlySpan<char> AttributeValueSpanAt(int index)
    {
        if (_arena is null)
        {
            ref readonly var attribute = ref _attributes![index];
            return attribute.ValueLength == 0 ? [] : _text!.AsSpan(attribute.ValueStart, attribute.ValueLength);
        }

        return _arena.FrozenAttributeValue(index).Span;
    }

    /// <summary>The value span for a payload index, without materializing a <see cref="CompactNodePayload" />.</summary>
    internal ReadOnlySpan<char> PayloadValueSpanAt(int payloadIndex)
    {
        if (_arena is null)
        {
            ref readonly var payload = ref _payloads![payloadIndex];
            return payload.ValueLength == 0 ? [] : _text!.AsSpan(payload.ValueStart, payload.ValueLength);
        }

        return _arena.FrozenPayloadValue(payloadIndex).Span;
    }

    /// <summary>Returns the first-attribute index and count for a node handle, or false when it has no payload.</summary>
    internal bool TryGetAttributeRange(int handle, out int firstAttribute, out int count)
    {
        var payloadIndex = PayloadIndexAt(handle);
        if (payloadIndex < 0)
        {
            firstAttribute = 0;
            count = 0;
            return false;
        }

        firstAttribute = PayloadFirstAttributeAt(payloadIndex);
        count = PayloadAttributeCountAt(payloadIndex);
        return true;
    }

    internal bool TryGetAttribute(int handle, ushort nameId, out CompactAttribute attribute, ref int inspected)
    {
        if (nameId != ushort.MaxValue && TryGetAttributeRange(handle, out var first, out var count))
            for (var index = first; index < first + count; index++)
            {
                inspected++;
                if (AttributeNameIdAt(index) == nameId)
                {
                    attribute = GetAttribute(index);
                    return true;
                }
            }

        attribute = default;
        return false;
    }

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

    internal string GetName(ushort id)
    {
        return id < GeneratedTagMetadata.KnownNameCount
            ? GeneratedTagMetadata.GetKnownName(id)
            : _names[id - GeneratedTagMetadata.KnownNameCount];
    }

    internal ReadOnlySpan<char> GetValue(int start, int length)
    {
        if (length == 0)
            return [];
        if (_arena is null)
            return _text!.AsSpan(start, length);
        var memory = IsAttributeValue(start)
            ? _arena.FrozenAttributeValue(DecodeValueIndex(start))
            : _arena.FrozenPayloadValue(DecodeValueIndex(start));
        return memory.Span[..length];
    }

    internal int GetParent(int handle)
    {
        if (!RetainsParentLinks)
            throw new InvalidOperationException("Parent links were not retained.");
        return _arena is null ? _parents![handle] : _arena.FrozenParent(handle);
    }

    internal bool TryGetSourceLocation(int handle, out CompactSourceLocation source)
    {
        if (_arena is not null)
        {
            if (RetainsSourceLocations)
                return _arena.TryGetFrozenSourceLocation(handle, out source);
            source = default;
            return false;
        }

        if (_sources is not null && _sources[handle].Index >= 0)
        {
            source = _sources[handle];
            return true;
        }

        source = default;
        return false;
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

    internal bool IsTemplate(int handle)
    {
        if (!_hasTemplates)
            return false;
        foreach (var boundary in _templateBoundaries)
            if (boundary.Handle == handle)
                return true;
        return false;
    }

    internal bool TryGetTemplateContent(int handle, out int contentStart)
    {
        if (!_hasTemplates)
        {
            contentStart = -1;
            return false;
        }

        foreach (var boundary in _templateBoundaries)
        {
            if (boundary.Handle != handle)
                continue;
            contentStart = boundary.ContentStart;
            return contentStart >= 0;
        }

        contentStart = -1;
        return false;
    }

    internal bool TryGetContainingTemplateContentEnd(int handle, out int contentEnd)
    {
        contentEnd = -1;
        if (!_hasTemplates)
            return false;
        foreach (var boundary in _templateBoundaries)
            if (handle >= boundary.ContentStart && handle < boundary.ContentEnd)
                contentEnd = Math.Max(contentEnd, boundary.ContentEnd);
        return contentEnd >= 0;
    }

    internal bool IsInSameTreeScope(int first, int second)
    {
        if (!_hasTemplates)
            return true;
        foreach (var boundary in _templateBoundaries)
        {
            var firstInContent = first >= boundary.ContentStart && first < boundary.ContentEnd;
            var secondInContent = second >= boundary.ContentStart && second < boundary.ContentEnd;
            if (firstInContent != secondInContent)
                return false;
        }

        return true;
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

    private static int EncodePayloadValue(int index)
    {
        return checked(index << 1);
    }

    private static int EncodeAttributeValue(int index)
    {
        return checked((index << 1) | 1);
    }

    private static bool IsAttributeValue(int value)
    {
        return (value & 1) != 0;
    }

    private static int DecodeValueIndex(int value)
    {
        return value >> 1;
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
