using System.Collections;
using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.ReadOnly.Html.Model;

internal class ReadOnlyNodeList : IConstructableNodeList, IReadOnlyNodeList
{
    internal readonly List<ReadOnlyNode> _nodes;

    public ReadOnlyNodeList()
    {
        _nodes = new List<ReadOnlyNode>(2);
    }

    public Int32 Length => _nodes.Count;

    public IConstructableNode this[Int32 index] => _nodes[index];
    IReadOnlyNode IReadOnlyNodeList.this[Int32 index] => (_nodes[index] as IReadOnlyNode)!;

    IEnumerator<IReadOnlyNode> IEnumerable<IReadOnlyNode>.GetEnumerator()
    {
        return _nodes.OfType<IReadOnlyNode>().GetEnumerator();
    }

    public IEnumerator<IConstructableNode> GetEnumerator()
    {
        return _nodes.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public void Add(IConstructableNode node)
    {
        _nodes.Add((ReadOnlyNode)node);
    }

    public void Remove(IConstructableNode node)
    {
        node.Parent = null;
        _nodes.Remove((ReadOnlyNode)node);
    }

    public void Clear()
    {
        foreach (var node in _nodes)
        {
            node.Parent = null;
        }
        _nodes.Clear();
    }

    public void Insert(int idx, IConstructableNode node)
    {
        _nodes.Insert(idx, (ReadOnlyNode)node);
    }
}