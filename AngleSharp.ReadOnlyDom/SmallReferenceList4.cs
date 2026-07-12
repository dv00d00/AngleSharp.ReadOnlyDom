namespace AngleSharp.ReadOnlyDom;

internal struct SmallReferenceList4<T>
    where T : class
{
    private T? _item0;
    private T? _item1;
    private T? _item2;
    private T? _item3;
    private T[]? _overflow;

    public int Count { get; private set; }

    public readonly T this[int index] =>
        index >= 0 && index < Count
            ? index switch
            {
                0 => _item0!,
                1 => _item1!,
                2 => _item2!,
                3 => _item3!,
                _ => _overflow![index - 4],
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
            throw new ArgumentOutOfRangeException(nameof(index));

        EnsureCapacity(Count + 1);
        for (var current = Count; current > index; current--)
            Set(current, this[current - 1]);
        Set(index, item);
        Count++;
    }

    public bool Remove(T item)
    {
        for (var index = 0; index < Count; index++)
        {
            if (!ReferenceEquals(this[index], item))
                continue;
            RemoveAt(index);
            return true;
        }

        return false;
    }

    public void RemoveAt(int index)
    {
        if ((uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        for (var current = index; current < Count - 1; current++)
            Set(current, this[current + 1]);
        ClearSlot(Count - 1);
        Count--;
    }

    public void Clear()
    {
        _item0 = null;
        _item1 = null;
        _item2 = null;
        _item3 = null;
        if (_overflow is not null)
            Array.Clear(_overflow, 0, Math.Min(_overflow.Length, Math.Max(0, Count - 4)));
        Count = 0;
    }

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

    private void Set(int index, T item)
    {
        switch (index)
        {
            case 0:
                _item0 = item;
                break;
            case 1:
                _item1 = item;
                break;
            case 2:
                _item2 = item;
                break;
            case 3:
                _item3 = item;
                break;
            default:
                _overflow![index - 4] = item;
                break;
        }
    }

    private void ClearSlot(int index)
    {
        switch (index)
        {
            case 0:
                _item0 = null;
                break;
            case 1:
                _item1 = null;
                break;
            case 2:
                _item2 = null;
                break;
            case 3:
                _item3 = null;
                break;
            default:
                _overflow![index - 4] = null!;
                break;
        }
    }
}
