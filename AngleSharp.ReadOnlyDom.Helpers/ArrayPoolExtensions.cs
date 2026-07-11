using System.Buffers;

namespace AngleSharp.ReadOnlyDom.Helpers;

public class Lease<T> : IDisposable
{
    private T[]? _data;
    private readonly int _requestedLength;

    public Lease(T[] data, int requestedLength)
    {
        _data = data;
        _requestedLength = requestedLength;
    }

    public int RequestedLength => _requestedLength;

    public T[] Data => _data!;

    public Span<T> Span => Data.AsSpan(0, RequestedLength);

    public Memory<T> Memory => Data.AsMemory(0, RequestedLength);

    public void Dispose()
    {
        if (_data != null)
        {
            ArrayPool<T>.Shared.Return(_data, false);
            _data = null;
        }
    }
}