using System.Buffers;

namespace AngleSharp.ReadOnlyDom.CompactPrototype.Arena;

internal sealed class PooledReferenceBuffer<T> : IDisposable
    where T : class
{
    private T[] _items;

    public PooledReferenceBuffer(int initialCapacity)
    {
        _items = ArrayPool<T>.Shared.Rent(initialCapacity);
    }

    public int Count { get; private set; }
    public T? this[int index]
    {
        get => index < Count ? _items[index] : throw new ArgumentOutOfRangeException(nameof(index));
        set => _items[index] = value!;
    }

    public void Add(T item)
    {
        if (Count == _items.Length)
            Grow();
        _items[Count++] = item;
    }

    // Reserves a slot without materializing a wrapper. Keeps handle == index alignment for leaf nodes
    // whose reference object is only created on demand (see Arena.Node).
    public void AddEmpty()
    {
        if (Count == _items.Length)
            Grow();
        _items[Count++] = null!;
    }

    public void Dispose()
    {
        var items = _items;
        if (items.Length == 0)
            return;
        _items = [];
        Count = 0;
        ArrayPool<T>.Shared.Return(items, clearArray: true);
    }

    private void Grow()
    {
        var next = ArrayPool<T>.Shared.Rent(checked(_items.Length * 2));
        _items.AsSpan(0, Count).CopyTo(next);
        ArrayPool<T>.Shared.Return(_items, clearArray: true);
        _items = next;
    }
}
