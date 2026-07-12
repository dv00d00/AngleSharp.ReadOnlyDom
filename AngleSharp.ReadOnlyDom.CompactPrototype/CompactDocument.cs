namespace AngleSharp.ReadOnlyDom.CompactPrototype;

public sealed class CompactDocument : IDisposable
{
    private readonly CompactNode[] _nodes;
    private readonly CompactAttribute[] _attributes;
    private readonly string[] _names;
    private readonly char[] _text;
    private readonly int[]? _parents;
    private readonly CompactSourceLocation[]? _sources;

    internal CompactDocument(
        CompactNode[] nodes,
        CompactAttribute[] attributes,
        string[] names,
        char[] text,
        int[]? parents,
        CompactSourceLocation[]? sources
    )
    {
        _nodes = nodes;
        _attributes = attributes;
        _names = names;
        _text = text;
        _parents = parents;
        _sources = sources;
    }

    public int NodeCount => _nodes.Length;
    public int AttributeCount => _attributes.Length;
    public int NameCount => _names.Length;
    public int TextLength => _text.Length;
    public bool HasParentLinks => _parents is not null;
    public bool HasSourceLocations => _sources is not null;

    public ref readonly CompactNode GetNode(int handle) => ref _nodes[handle];

    public ref readonly CompactAttribute GetAttribute(int handle) => ref _attributes[handle];

    public string GetName(ushort nameId) => _names[nameId];

    public ReadOnlySpan<char> GetValue(int start, int length) => _text.AsSpan(start, length);

    public int GetParent(int handle) =>
        _parents is null ? throw new InvalidOperationException("Parent links were not retained.") : _parents[handle];

    public bool TryGetSourceLocation(int handle, out CompactSourceLocation location)
    {
        if (_sources is not null)
        {
            location = _sources[handle];
            return location.Index >= 0;
        }

        location = default;
        return false;
    }

    public IEnumerable<int> Children(int handle)
    {
        var child = _nodes[handle].FirstChild;
        while (child >= 0)
        {
            yield return child;
            child = _nodes[child].NextSibling;
        }
    }

    public IEnumerable<int> FindElements(string localName)
    {
        for (var handle = 0; handle < _nodes.Length; handle++)
        {
            var node = _nodes[handle];
            if (
                node.Kind == CompactNodeKind.Element
                && GetName(node.NameId).Equals(localName, StringComparison.Ordinal)
            )
                yield return handle;
        }
    }

    public CompactNodeWrapper MaterializeWrapperTree() => MaterializeWrapper(0);

    private CompactNodeWrapper MaterializeWrapper(int handle)
    {
        var children = Children(handle).Select(MaterializeWrapper).ToArray();
        return new CompactNodeWrapper(this, handle, children);
    }

    public void Dispose() { }
}

public sealed class CompactNodeWrapper
{
    internal CompactNodeWrapper(CompactDocument document, int handle, CompactNodeWrapper[] children)
    {
        Document = document;
        Handle = handle;
        Children = children;
    }

    public CompactDocument Document { get; }
    public int Handle { get; }
    public IReadOnlyList<CompactNodeWrapper> Children { get; }
    public ref readonly CompactNode Node => ref Document.GetNode(Handle);
    public string Name => Document.GetName(Node.NameId);
}
