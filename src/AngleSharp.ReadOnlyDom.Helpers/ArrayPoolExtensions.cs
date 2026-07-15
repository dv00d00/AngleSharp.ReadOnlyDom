using System.Buffers;

namespace AngleSharp.ReadOnlyDom.Helpers;

public class Lease<T>(T[] data, int requestedLength) : IDisposable
{
    private T[]? _data = data;

    public int RequestedLength { get; } = requestedLength;

    public T[] Data => _data!;

    public Span<T> Span => Data.AsSpan(0, RequestedLength);

    public Memory<T> Memory => Data.AsMemory(0, RequestedLength);

    public void Dispose()
    {
        if (_data != null)
        {
            ArrayPool<T>.Shared.Return(_data);
            _data = null;
        }
    }
}
