namespace AngleSharp.ReadOnlyDom.CompactPrototype;

/// <summary>
/// An indexed, read-only view over a source document. Value memories borrow their backing storage from the
/// source input; callers must keep that storage valid for the lifetime of this document.
/// </summary>
public sealed class CompactDocument : IDisposable
{
    private readonly CompactNode[] _nodes;
    private readonly CompactAttribute[] _attributes;
    private readonly IReadOnlyList<string> _names;
    private readonly ReadOnlyMemory<char>[] _values;
    private readonly int[]? _parents;
    private readonly INodePayloadIndex<CompactSourceLocation>? _sources;

    internal CompactDocument(
        CompactNode[] nodes,
        CompactAttribute[] attributes,
        IReadOnlyList<string> names,
        ReadOnlyMemory<char>[] values,
        int[]? parents,
        INodePayloadIndex<CompactSourceLocation>? sources
    )
    {
        _nodes = nodes;
        _attributes = attributes;
        _names = names;
        _values = values;
        _parents = parents;
        _sources = sources;
    }

    public int NodeCount => _nodes.Length;
    public int AttributeCount => _attributes.Length;
    public int NameCount => _names.Count;
    public int ValueCount => _values.Length;
    public bool HasParentLinks => _parents is not null;
    public bool HasSourceLocations => _sources is not null;
    public CompactIndexMode SourceLocationIndexMode => _sources?.Mode ?? CompactIndexMode.None;

    public ref readonly CompactNode GetNode(int handle) => ref _nodes[handle];

    public ref readonly CompactAttribute GetAttribute(int handle) => ref _attributes[handle];

    public string GetName(ushort nameId) => _names[nameId];

    public ReadOnlySpan<char> GetValue(int valueIndex, int length) =>
        valueIndex < 0 ? ReadOnlySpan<char>.Empty : _values[valueIndex].Span[..length];

    public int GetParent(int handle) =>
        _parents?[handle] ?? throw new InvalidOperationException("Parent links were not retained.");

    public bool TryGetSourceLocation(int handle, out CompactSourceLocation location)
    {
        if (_sources is not null)
            return _sources.TryGetValue(handle, out location);
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

internal interface INodePayloadIndex<T>
{
    CompactIndexMode Mode { get; }
    bool TryGetValue(int handle, out T value);
}

internal sealed class DenseNodePayloadIndex<T>(T[] values, bool[] present) : INodePayloadIndex<T>
{
    public CompactIndexMode Mode => CompactIndexMode.Dense;

    public bool TryGetValue(int handle, out T value)
    {
        value = values[handle];
        return present[handle];
    }
}

internal sealed class SparseNodePayloadIndex<T>(int[] handles, T[] values) : INodePayloadIndex<T>
{
    public CompactIndexMode Mode => CompactIndexMode.Sparse;

    public bool TryGetValue(int handle, out T value)
    {
        var index = Array.BinarySearch(handles, handle);
        if (index >= 0)
        {
            value = values[index];
            return true;
        }

        value = default!;
        return false;
    }
}

internal sealed class DictionaryNodePayloadIndex<T>(Dictionary<int, T> values) : INodePayloadIndex<T>
{
    public CompactIndexMode Mode => CompactIndexMode.Dictionary;

    public bool TryGetValue(int handle, out T value) => values.TryGetValue(handle, out value!);
}
