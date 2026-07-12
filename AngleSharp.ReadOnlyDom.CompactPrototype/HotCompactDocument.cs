using System.Buffers;
using System.Runtime.InteropServices;

namespace AngleSharp.ReadOnlyDom.CompactPrototype;

[StructLayout(LayoutKind.Sequential)]
public readonly struct HotCompactNode
{
    internal HotCompactNode(
        int firstChild,
        int nextSibling,
        int payloadIndex,
        ushort nameId,
        CompactNodeKind kind,
        byte hotFlags
    )
    {
        FirstChild = firstChild;
        NextSibling = nextSibling;
        PayloadIndex = payloadIndex;
        NameId = nameId;
        Kind = kind;
        HotFlags = hotFlags;
    }

    public int FirstChild { get; }
    public int NextSibling { get; }
    public int PayloadIndex { get; }
    public ushort NameId { get; }
    public CompactNodeKind Kind { get; }
    public byte HotFlags { get; }
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct ColdNodePayload
{
    internal ColdNodePayload(int firstAttribute, int valueStart, int valueLength, ushort attributeCount)
    {
        FirstAttribute = firstAttribute;
        ValueStart = valueStart;
        ValueLength = valueLength;
        AttributeCount = attributeCount;
    }

    public int FirstAttribute { get; }
    public int ValueStart { get; }
    public int ValueLength { get; }
    public ushort AttributeCount { get; }
}

public enum CompactBufferOwnership
{
    Owned,
    Pooled,
}

public sealed class HotCompactDocument : IDisposable
{
    private readonly HotCompactNode[] _nodes;
    private readonly ColdNodePayload[] _payloads;
    private readonly CompactAttribute[] _attributes;
    private readonly string[] _names;
    private readonly char[] _text;
    private readonly int[]? _parents;
    private readonly CompactSourceLocation[]? _sources;

    private readonly int _nodeCount;
    private readonly int _payloadCount;
    private readonly int _attributeCount;
    private readonly int _nameCount;
    private readonly int _textLength;
    private readonly bool _pooled;
    private int _disposed;

    internal HotCompactDocument(
        HotCompactNode[] nodes,
        ColdNodePayload[] payloads,
        CompactAttribute[] attributes,
        string[] names,
        char[] text,
        int[]? parents,
        CompactSourceLocation[]? sources,
        int nodeCount,
        int payloadCount,
        int attributeCount,
        int nameCount,
        int textLength,
        bool pooled
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
        _pooled = pooled;
    }

    public int NodeCount => _nodeCount;
    public int AttributeCount => _attributeCount;
    public int PayloadCount => _payloadCount;
    public int TextLength => _textLength;
    public bool HasParentLinks => _parents is not null;
    public bool HasSourceLocations => _sources is not null;

    public ref readonly HotCompactNode GetNode(int handle) => ref _nodes[handle];

    public ref readonly ColdNodePayload GetPayload(int index) => ref _payloads[index];

    public ref readonly CompactAttribute GetAttribute(int index) => ref _attributes[index];

    public string GetName(ushort id) => _names[id];

    public ReadOnlySpan<char> GetValue(int start, int length) => _text.AsSpan(start, length);

    public int GetParent(int handle) =>
        _parents is null ? throw new InvalidOperationException("Parent links were not retained.") : _parents[handle];

    public bool TryGetSourceLocation(int handle, out CompactSourceLocation source)
    {
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
        foreach (ref readonly var node in _nodes.AsSpan(0, _nodeCount))
            if (node.Kind == CompactNodeKind.Element && node.NameId == nameId)
                count++;
        return count;
    }

    public ushort FindNameId(string name)
    {
        for (ushort i = 0; i < _nameCount; i++)
            if (_names[i].Equals(name, StringComparison.Ordinal))
                return i;
        return ushort.MaxValue;
    }

    public void Dispose()
    {
        if (!_pooled || Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        ArrayPool<HotCompactNode>.Shared.Return(_nodes);
        ArrayPool<ColdNodePayload>.Shared.Return(_payloads);
        ArrayPool<CompactAttribute>.Shared.Return(_attributes);
        ArrayPool<string>.Shared.Return(_names, clearArray: true);
        ArrayPool<char>.Shared.Return(_text, clearArray: true);
        if (_parents is not null)
            ArrayPool<int>.Shared.Return(_parents);
        if (_sources is not null)
            ArrayPool<CompactSourceLocation>.Shared.Return(_sources);
    }
}
