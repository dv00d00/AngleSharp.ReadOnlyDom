using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.IO;

namespace AngleSharp.ReadOnlyDom.Helpers;

public static class HttpClientExtensions
{
    private const int RingBufferSize = 16;

    private static readonly ConcurrentDictionary<string, RingBuffer<int>> AvgResponseSize = new();

    private static readonly RecyclableMemoryStreamManager Manager = new RecyclableMemoryStreamManager();

    public static async Task<ArrayPoolExtensions.Lease<char>> DownloadChars(
        this HttpClient client,
        HttpRequestMessage request,
        string? id = null,
        int? expectedResponseSize = null)
    {
        id ??= request.RequestUri!.Host;
        using var postResponse = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        var rb = AvgResponseSize.GetOrAdd(id, static _ => new RingBuffer<int>(RingBufferSize));
        int size = rb.Avg() ?? (int?)postResponse.Content.Headers.ContentLength ?? expectedResponseSize ?? 0;

        await using var htmlStream = await postResponse.Content.ReadAsStreamAsync();
        await using var cachedStream = Manager.GetStream(request.RequestUri!.Host, size);
        await htmlStream.CopyToAsync(cachedStream);

        var totalBytes = (int)cachedStream.Length;
        rb.Add(totalBytes);

        var maxTotalChars = Encoding.UTF8.GetMaxCharCount(totalBytes);
        var charsBuffer = ArrayPool<char>.Shared.Rent(maxTotalChars);
        int writtenChars = Encoding.UTF8.GetChars(cachedStream.GetReadOnlySequence(), charsBuffer.AsSpan());

        return new ArrayPoolExtensions.Lease<char>(ArrayPool<char>.Shared, charsBuffer, writtenChars);
    }
}