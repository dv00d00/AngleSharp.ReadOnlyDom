using System.Collections;
using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.Html.Model;

internal class ReadOnlyNodeList : IConstructableNodeList, IReadOnlyNodeList
{
    private SmallReferenceList<ReadOnlyNode> _nodes;

    public int Length => _nodes.Count;
    public IConstructableNode this[int index] => _nodes[index];
    IReadOnlyNode IReadOnlyNodeList.this[int index] => _nodes[index];

    IEnumerator<IReadOnlyNode> IEnumerable<IReadOnlyNode>.GetEnumerator()
    {
        for (var i = 0; i < _nodes.Count; i++)
        {
            yield return _nodes[i];
        }
    }

    public IEnumerator<IConstructableNode> GetEnumerator()
    {
        for (var i = 0; i < _nodes.Count; i++)
        {
            yield return _nodes[i];
        }
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
        _nodes.Remove((ReadOnlyNode)node);
    }

    public void RemoveAt(Int32 idx)
    {
        _nodes.RemoveAt(idx);
    }

    public void Clear()
    {
        for (var i = 0; i < _nodes.Count; i++)
        {
            _nodes[i].Parent = null;
        }
        _nodes.Clear();
    }

    public void Insert(int idx, IConstructableNode node)
    {
        _nodes.Insert(idx, (ReadOnlyNode)node);
    }
}
