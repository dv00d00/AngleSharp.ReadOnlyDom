using System.Buffers;
using System.Runtime.CompilerServices;

namespace AngleSharp.ReadOnlyDom.CompactPrototype.Arena;

internal sealed class PooledValueBuffer<T> : IDisposable
{
    private T[] _items;

    public PooledValueBuffer(int initialCapacity)
    {
        _items = ArrayPool<T>.Shared.Rent(initialCapacity);
    }

    public int Count { get; private set; }
    public ref T this[int index] => ref _items[index];

    public int Add(T item)
    {
        if (Count == _items.Length)
            Grow();
        var index = Count++;
        _items[index] = item;
        return index;
    }

    public void AddRange(ReadOnlySpan<T> items)
    {
        EnsureCapacity(checked(Count + items.Length));
        items.CopyTo(_items.AsSpan(Count));
        Count += items.Length;
    }

    public (T[] Items, int Count) Detach()
    {
        var items = _items;
        var count = Count;
        _items = [];
        Count = 0;
        return (items, count);
    }

    public void Dispose()
    {
        if (_items.Length != 0)
            ArrayPool<T>.Shared.Return(_items, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        _items = [];
        Count = 0;
    }

    private void Grow()
    {
        EnsureCapacity(checked(_items.Length * 2));
    }

    private void EnsureCapacity(int capacity)
    {
        if (capacity <= _items.Length)
            return;
        var nextCapacity = _items.Length;
        while (nextCapacity < capacity)
            nextCapacity = checked(nextCapacity * 2);
        var next = ArrayPool<T>.Shared.Rent(nextCapacity);
        _items.AsSpan(0, Count).CopyTo(next);
        ArrayPool<T>.Shared.Return(_items, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        _items = next;
    }
}
