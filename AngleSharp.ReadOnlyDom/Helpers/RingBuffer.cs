using System.Numerics;

namespace AngleSharp.ReadOnlyDom.Helpers;

public class RingBuffer<T> where T : struct, INumber<T>
{
    private readonly T[] _buffer;
    private int _end;

    public RingBuffer(int capacity)
    {
        _buffer = new T[capacity];
        _end = 0;
    }

    public int Count => Math.Min(_buffer.Length, _end);

    public void Add(T item)
    {
        _buffer[_end % _buffer.Length] = item;
        _end++;
    }

    public T? Avg()
    {
        var count = Count;
        if (count == 0) return null;
        T sum = T.Zero;
        for (int i = 0; i < count; i++)
        {
            sum += _buffer[i];
        }

        return sum / T.CreateChecked(count);
    }
}