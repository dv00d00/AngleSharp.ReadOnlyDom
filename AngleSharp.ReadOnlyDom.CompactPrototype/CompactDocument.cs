using System.Buffers;

namespace AngleSharp.ReadOnlyDom.CompactPrototype;

public sealed class CompactDocument : IDisposable
{
    private readonly CompactNode[] _nodes;
    private readonly CompactNodePayload[] _payloads;
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

    public int NodeCount => _nodeCount;
    public int AttributeCount => _attributeCount;
    public int PayloadCount => _payloadCount;
    public int TextLength => _textLength;
    public bool HasParentLinks => _parents is not null;
    public bool HasSourceLocations => _sources is not null;

    public ref readonly CompactNode GetNode(int handle) => ref _nodes[handle];

    public ref readonly CompactNodePayload GetPayload(int index) => ref _payloads[index];

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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        ArrayPool<CompactNode>.Shared.Return(_nodes);
        ArrayPool<CompactNodePayload>.Shared.Return(_payloads);
        ArrayPool<CompactAttribute>.Shared.Return(_attributes);
        ArrayPool<string>.Shared.Return(_names, clearArray: true);
        ArrayPool<char>.Shared.Return(_text, clearArray: true);
        if (_parents is not null)
            ArrayPool<int>.Shared.Return(_parents);
        if (_sources is not null)
            ArrayPool<CompactSourceLocation>.Shared.Return(_sources);
    }
}
