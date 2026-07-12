namespace AngleSharp.ReadOnlyDom;

internal struct SmallReferenceList<T>
    where T : class
{
    private T? _first;
    private T? _second;
    private T[]? _overflow;

    public int Count { get; private set; }

    public readonly T this[int index] =>
        index >= 0 && index < Count
            ? index switch
            {
                0 => _first!,
                1 => _second!,
                _ => _overflow![index - 2],
            }
            : throw new ArgumentOutOfRangeException(nameof(index));

    public void Add(T item)
    {
        EnsureCapacity(Count + 1);
        Set(Count, item);
        Count++;
    }

    public void Insert(int index, T item)
    {
        if ((uint)index > (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        EnsureCapacity(Count + 1);
        for (var i = Count; i > index; i--)
        {
            Set(i, this[i - 1]);
        }

        Set(index, item);
        Count++;
    }

    public bool Remove(T item)
    {
        for (var i = 0; i < Count; i++)
        {
            if (ReferenceEquals(this[i], item))
            {
                RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    public void RemoveAt(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        for (var i = index; i < Count - 1; i++)
        {
            Set(i, this[i + 1]);
        }

        ClearSlot(Count - 1);
        Count--;
    }

    public void Clear()
    {
        _first = null;
        _second = null;
        if (_overflow is not null)
        {
            Array.Clear(_overflow, 0, Math.Min(_overflow.Length, Math.Max(0, Count - 2)));
        }

        Count = 0;
    }

    private void EnsureCapacity(int count)
    {
        var overflowCount = count - 2;
        if (overflowCount <= 0 || _overflow is { Length: var length } && length >= overflowCount)
        {
            return;
        }

        var newLength = _overflow is null ? 2 : _overflow.Length * 2;
        while (newLength < overflowCount)
        {
            newLength *= 2;
        }

        Array.Resize(ref _overflow, newLength);
    }

    private void Set(int index, T item)
    {
        switch (index)
        {
            case 0:
                _first = item;
                break;
            case 1:
                _second = item;
                break;
            default:
                _overflow![index - 2] = item;
                break;
        }
    }

    private void ClearSlot(int index)
    {
        switch (index)
        {
            case 0:
                _first = null;
                break;
            case 1:
                _second = null;
                break;
            default:
                _overflow![index - 2] = null!;
                break;
        }
    }
}
