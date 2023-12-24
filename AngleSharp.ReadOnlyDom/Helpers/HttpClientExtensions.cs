using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.IO;

namespace AngleSharp.ReadOnlyDom.Helpers;

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