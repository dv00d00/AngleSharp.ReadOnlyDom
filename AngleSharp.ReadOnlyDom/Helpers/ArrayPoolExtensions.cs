using System.Buffers;

namespace AngleSharp.ReadOnlyDom.Helpers;

public static class ArrayPoolExtensions
{
    internal static Lease<T> Borrow<T>(this ArrayPool<T> pool, int length)
    {
        var arr = ArrayPool<T>.Shared.Rent(length);
        return new Lease<T>(ArrayPool<T>.Shared, arr, length);
    }
        
    public readonly struct Lease<T> : IDisposable
    {
        private readonly ArrayPool<T> _owner;
        private readonly T[] _data;
        private readonly int _requestedLength;

        public Lease(ArrayPool<T> owner, T[] data, int requestedLength)
        {
            _owner = owner;
            _data = data;
            _requestedLength = requestedLength;
        }

        public int RequestedLength => _requestedLength;

        public T[] Data => _data;

        public Span<T> Span => Data.AsSpan(0, RequestedLength);

        public Memory<T> Memory => Data.AsMemory(0, RequestedLength);

        public void Dispose()
        {
            _owner.Return(_data, false);
        }
    }
}