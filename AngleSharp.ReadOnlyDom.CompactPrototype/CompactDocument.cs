using System.Buffers;
using AngleSharp.Text;
using ArenaStorage = AngleSharp.ReadOnlyDom.CompactPrototype.Arena.Arena;

namespace AngleSharp.ReadOnlyDom.CompactPrototype;

public sealed class CompactDocument : IDisposable
{
    private readonly CompactNode[]? _nodes;
    private readonly CompactNodePayload[]? _payloads;
    private readonly CompactAttribute[]? _attributes;
    private readonly char[]? _text;
    private readonly int[]? _parents;
    private readonly CompactSourceLocation[]? _sources;

    private readonly ArenaStorage? _arena;
    private readonly TextSource? _source;
    private readonly ushort[]? _nodeNameIds;
    private readonly ushort[]? _attributeNameIds;
    private readonly CompactMetadataOptions _metadataOptions;

    private readonly string[] _names;
    private readonly int _nodeCount;
    private readonly int _payloadCount;
    private readonly int _attributeCount;
    private readonly int _nameCount;
    private readonly int _textLength;
    private int _disposed;

    internal CompactDocument(
        CompactNode[] nodes,
        CompactNodePayload[] payloads,
        CompactAttribute[] attributes,
        string[] names,
        char[] text,
        int[]? parents,
        CompactSourceLocation[]? sources,
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
        _nodeCount = nodeCount;
        _payloadCount = payloadCount;
        _attributeCount = attributeCount;
        _nameCount = nameCount;
        _textLength = textLength;
    }

    internal CompactDocument(
        ArenaStorage arena,
        TextSource source,
        ushort[] nodeNameIds,
        ushort[] attributeNameIds,
        string[] names,
        int nodeCount,
        int payloadCount,
        int attributeCount,
        int nameCount,
        int textLength,
        CompactMetadataOptions metadataOptions
    )
    {
        _arena = arena;
        _source = source;
        _nodeNameIds = nodeNameIds;
        _attributeNameIds = attributeNameIds;
        _names = names;
        _nodeCount = nodeCount;
        _payloadCount = payloadCount;
        _attributeCount = attributeCount;
        _nameCount = nameCount;
        _textLength = textLength;
        _metadataOptions = metadataOptions;
    }

    public CompactDocumentLayout Layout =>
        _arena is null ? CompactDocumentLayout.Packed : CompactDocumentLayout.FrozenColumns;
    public int NodeCount => _nodeCount;
    public int AttributeCount => _attributeCount;
    public int PayloadCount => _payloadCount;
    public int TextLength => _textLength;
    public bool HasParentLinks =>
        _arena is null ? _parents is not null : _metadataOptions.HasFlag(CompactMetadataOptions.ParentLinks);
    public bool HasSourceLocations =>
        _arena is null ? _sources is not null : _metadataOptions.HasFlag(CompactMetadataOptions.SourceLocations);

    public CompactNode GetNode(int handle)
    {
        if (_arena is null)
            return _nodes![handle];
        return new CompactNode(
            _arena.FrozenFirstChild(handle),
            _arena.FrozenNextSibling(handle),
            _arena.FrozenPayloadIndex(handle),
            _nodeNameIds![handle],
            _arena.FrozenKind(handle),
            _arena.FrozenFlags(handle)
        );
    }

    public CompactNodePayload GetPayload(int index)
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

    public CompactAttribute GetAttribute(int index)
    {
        if (_arena is null)
            return _attributes![index];
        var value = _arena.FrozenAttributeValue(index);
        return new CompactAttribute(
            _attributeNameIds![index],
            value.IsEmpty ? -1 : EncodeAttributeValue(index),
            value.Length
        );
    }

    public string GetName(ushort id) =>
        id < GeneratedTagMetadata.KnownNameCount
            ? GeneratedTagMetadata.GetKnownName(id)
            : _names[id - GeneratedTagMetadata.KnownNameCount];

    public ReadOnlySpan<char> GetValue(int start, int length)
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

    public int GetParent(int handle)
    {
        if (!HasParentLinks)
            throw new InvalidOperationException("Parent links were not retained.");
        return _arena is null ? _parents![handle] : _arena.FrozenParent(handle);
    }

    public bool TryGetSourceLocation(int handle, out CompactSourceLocation source)
    {
        if (_arena is not null)
        {
            if (HasSourceLocations)
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

    public int CountElements(ushort nameId)
    {
        var count = 0;
        for (var handle = 0; handle < _nodeCount; handle++)
        {
            var node = GetNode(handle);
            if (node.Kind == CompactNodeKind.Element && node.NameId == nameId)
                count++;
        }
        return count;
    }

    public ushort FindNameId(string name)
    {
        if (GeneratedTagMetadata.TryGetKnownNameId(name.AsSpan(), out var knownId))
            return ContainsNameId(knownId) ? knownId : ushort.MaxValue;
        for (ushort i = 0; i < _nameCount; i++)
            if (_names[i].Equals(name, StringComparison.Ordinal))
                return checked((ushort)(GeneratedTagMetadata.KnownNameCount + i));
        return ushort.MaxValue;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_names.Length != 0)
            ArrayPool<string>.Shared.Return(_names, clearArray: true);
        if (_arena is not null)
        {
            ArrayPool<ushort>.Shared.Return(_nodeNameIds!);
            ArrayPool<ushort>.Shared.Return(_attributeNameIds!);
            try
            {
                _arena.Dispose();
            }
            finally
            {
                _source!.Dispose();
            }
            return;
        }

        ArrayPool<CompactNode>.Shared.Return(_nodes!);
        ArrayPool<CompactNodePayload>.Shared.Return(_payloads!);
        ArrayPool<CompactAttribute>.Shared.Return(_attributes!);
        ArrayPool<char>.Shared.Return(_text!, clearArray: true);
        if (_parents is not null)
            ArrayPool<int>.Shared.Return(_parents);
        if (_sources is not null)
            ArrayPool<CompactSourceLocation>.Shared.Return(_sources);
    }

    private static int EncodePayloadValue(int index) => checked(index << 1);

    private static int EncodeAttributeValue(int index) => checked((index << 1) | 1);

    private static bool IsAttributeValue(int value) => (value & 1) != 0;

    private static int DecodeValueIndex(int value) => value >> 1;

    private bool ContainsNameId(ushort id)
    {
        for (var handle = 0; handle < _nodeCount; handle++)
        {
            if (GetNode(handle).NameId == id)
                return true;
        }
        for (var attribute = 0; attribute < _attributeCount; attribute++)
        {
            if (GetAttribute(attribute).NameId == id)
                return true;
        }
        return false;
    }
}
