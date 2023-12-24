using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;
using System.Text;
using Microsoft.IO;

namespace AngleSharp.Benchmarks.UserCode;

public class RingBuffer<T> where T : struct, INumber<T>
{
    private readonly T[] _buffer;
    private Int32 _end;

    public RingBuffer(Int32 capacity)
    {
        _buffer = new T[capacity];
        _end = 0;
    }

    public Int32 Count => Math.Min(_buffer.Length, _end);

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

public static class ArrayPoolExtensions
{
    internal static Lease<T> Borrow<T>(this ArrayPool<T> pool, Int32 length)
    {
        var arr = ArrayPool<T>.Shared.Rent(length);
        return new Lease<T>(ArrayPool<T>.Shared, arr, length);
    }
        
    public readonly struct Lease<T> : IDisposable
    {
        private readonly ArrayPool<T> _owner;
        private readonly T[] _data;
        private readonly Int32 _requestedLength;

        public Lease(ArrayPool<T> owner, T[] data, Int32 requestedLength)
        {
            _owner = owner;
            _data = data;
            _requestedLength = requestedLength;
        }

        public Int32 RequestedLength => _requestedLength;

        public T[] Data => _data;

        public Span<T> Span => Data.AsSpan(0, RequestedLength);

        public Memory<T> Memory => Data.AsMemory(0, RequestedLength);

        public void Dispose()
        {
            _owner.Return(_data, false);
        }
    }
}

public static class HttpClientExtensions
{
    private const int RingBufferSize = 16;

    private static readonly ConcurrentDictionary<String, RingBuffer<Int32>> AvgResponseSize = new();

    private static readonly RecyclableMemoryStreamManager Manager = new RecyclableMemoryStreamManager();

    public static async Task<ArrayPoolExtensions.Lease<Char>> DownloadChars(
        this HttpClient client,
        HttpRequestMessage request,
        String? id = null,
        Int32? expectedResponseSize = null)
    {
        id ??= request.RequestUri!.Host;
        using var postResponse = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        var rb = AvgResponseSize.GetOrAdd(id, static _ => new RingBuffer<Int32>(RingBufferSize));
        int size = rb.Avg() ?? (Int32?)postResponse.Content.Headers.ContentLength ?? expectedResponseSize ?? 0;

        await using var htmlStream = await postResponse.Content.ReadAsStreamAsync();
        await using var cachedStream = Manager.GetStream(request.RequestUri!.Host, size);
        await htmlStream.CopyToAsync(cachedStream);

        var totalBytes = (Int32)cachedStream.Length;
        rb.Add(totalBytes);

        var maxTotalChars = Encoding.UTF8.GetMaxCharCount(totalBytes);
        var charsBuffer = ArrayPool<Char>.Shared.Rent(maxTotalChars);
        int writtenChars = Encoding.UTF8.GetChars(cachedStream.GetReadOnlySequence(), charsBuffer.AsSpan());

        return new ArrayPoolExtensions.Lease<Char>(ArrayPool<Char>.Shared, charsBuffer, writtenChars);
    }
}