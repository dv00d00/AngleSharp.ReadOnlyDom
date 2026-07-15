using System.Collections;
using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.Html.Model;

internal class ReadOnlyNodeList : IConstructableNodeList, IReadOnlyNodeList
{
    private ReadOnlyNode? _node0;
    private ReadOnlyNode? _node1;
    private ReadOnlyNode? _node2;
    private ReadOnlyNode? _node3;
    private ReadOnlyNode[]? _overflow;
    private int _count;

    public int Length => _count;
    public IConstructableNode this[int index] => Get(index);
    IReadOnlyNode IReadOnlyNodeList.this[int index] => Get(index);

    IEnumerator<IReadOnlyNode> IEnumerable<IReadOnlyNode>.GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return Get(i);
        }
    }

    public IEnumerator<IConstructableNode> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return Get(i);
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public void Add(IConstructableNode node)
    {
        EnsureCapacity(_count + 1);
        Set(_count++, (ReadOnlyNode)node);
    }

    public void Remove(IConstructableNode node)
    {
        var target = (ReadOnlyNode)node;
        for (var index = 0; index < _count; index++)
        {
            if (!ReferenceEquals(Get(index), target))
                continue;
            RemoveAt(index);
            return;
        }
    }

    public void RemoveAt(Int32 idx)
    {
        if ((uint)idx >= (uint)_count)
            throw new ArgumentOutOfRangeException(nameof(idx));
        for (var index = idx; index < _count - 1; index++)
            Set(index, Get(index + 1));
        ClearSlot(--_count);
    }

    public void Clear()
    {
        for (var i = 0; i < _count; i++)
        {
            Get(i).Parent = null;
        }
        _node0 = null;
        _node1 = null;
        _node2 = null;
        _node3 = null;
        if (_overflow is not null)
            Array.Clear(_overflow, 0, Math.Min(_overflow.Length, Math.Max(0, _count - 4)));
        _count = 0;
    }

    public void Insert(int idx, IConstructableNode node)
    {
        if ((uint)idx > (uint)_count)
            throw new ArgumentOutOfRangeException(nameof(idx));
        EnsureCapacity(_count + 1);
        for (var index = _count; index > idx; index--)
            Set(index, Get(index - 1));
        Set(idx, (ReadOnlyNode)node);
        _count++;
    }

    private ReadOnlyNode Get(int index) =>
        index >= 0 && index < _count
            ? index switch
            {
                0 => _node0!,
                1 => _node1!,
                2 => _node2!,
                3 => _node3!,
                _ => _overflow![index - 4],
            }
            : throw new ArgumentOutOfRangeException(nameof(index));

    private void EnsureCapacity(int count)
    {
        var overflowCount = count - 4;
        if (overflowCount <= 0 || _overflow is { Length: var length } && length >= overflowCount)
            return;

        var newLength = _overflow is null ? 4 : _overflow.Length * 2;
        while (newLength < overflowCount)
            newLength *= 2;
        Array.Resize(ref _overflow, newLength);
    }

    private void Set(int index, ReadOnlyNode node)
    {
        switch (index)
        {
            case 0:
                _node0 = node;
                break;
            case 1:
                _node1 = node;
                break;
            case 2:
                _node2 = node;
                break;
            case 3:
                _node3 = node;
                break;
            default:
                _overflow![index - 4] = node;
                break;
        }
    }

    private void ClearSlot(int index)
    {
        switch (index)
        {
            case 0:
                _node0 = null;
                break;
            case 1:
                _node1 = null;
                break;
            case 2:
                _node2 = null;
                break;
            case 3:
                _node3 = null;
                break;
            default:
                _overflow![index - 4] = null!;
                break;
        }
    }
}
