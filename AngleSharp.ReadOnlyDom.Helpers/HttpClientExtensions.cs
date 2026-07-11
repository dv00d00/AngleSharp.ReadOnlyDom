using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.IO;

namespace AngleSharp.ReadOnlyDom.Helpers;

public static class HttpClientExtensions
{
    private const int RingBufferSize = 16;
    
    private static readonly Encoding DefaultStringEncoding = Encoding.UTF8;

    private const int UTF8CodePage = 65001;
    private const int UTF8PreambleLength = 3;
    private const byte UTF8PreambleByte0 = 0xEF;
    private const byte UTF8PreambleByte1 = 0xBB;
    private const byte UTF8PreambleByte2 = 0xBF;
    private const int UTF8PreambleFirst2Bytes = 0xEFBB;

    private const int UTF32CodePage = 12000;
    private const int UTF32PreambleLength = 4;
    private const byte UTF32PreambleByte0 = 0xFF;
    private const byte UTF32PreambleByte1 = 0xFE;
    private const byte UTF32PreambleByte2 = 0x00;
    private const byte UTF32PreambleByte3 = 0x00;
    private const int UTF32OrUnicodePreambleFirst2Bytes = 0xFFFE;

    private const int UnicodeCodePage = 1200;
    private const int UnicodePreambleLength = 2;
    private const byte UnicodePreambleByte0 = 0xFF;
    private const byte UnicodePreambleByte1 = 0xFE;

    private const int BigEndianUnicodeCodePage = 1201;
    private const int BigEndianUnicodePreambleLength = 2;
    private const byte BigEndianUnicodePreambleByte0 = 0xFE;
    private const byte BigEndianUnicodePreambleByte1 = 0xFF;
    private const int BigEndianUnicodePreambleFirst2Bytes = 0xFEFF;

    private static readonly ConcurrentDictionary<string, RingBuffer<int>> AvgResponseSize = new();

    private static readonly RecyclableMemoryStreamManager Manager = new RecyclableMemoryStreamManager();

    public static async Task<Lease<char>> DownloadChars(
        this HttpClient client,
        HttpRequestMessage request,
        string? endpointId = null,
        int? expectedResponseSize = null)
    {
        using var postResponse = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return await postResponse.DownloadChars(endpointId, expectedResponseSize);
    }
    
    public static async Task<Lease<char>> DownloadChars(
        this HttpResponseMessage postResponse,
        string? endpointId = null,
        int? expectedResponseSize = null)
    {
        endpointId ??= postResponse.RequestMessage?.RequestUri?.Host ?? "unknown";
        
        var rb = AvgResponseSize.GetOrAdd(endpointId, static _ => new RingBuffer<int>(RingBufferSize));
        int size = rb.Avg() ?? (int?)postResponse.Content.Headers.ContentLength ?? expectedResponseSize ?? 0;
        
        await using var htmlStream = await postResponse.Content.ReadAsStreamAsync();
        await using var recyclableMemoryStream = Manager.GetStream(endpointId, size);
        await htmlStream.CopyToAsync(recyclableMemoryStream);
        var totalBytes = (int)recyclableMemoryStream.Length;
        rb.Add(totalBytes);
        var readOnlySequence = recyclableMemoryStream.GetReadOnlySequence();
        
        var (encoding, offset) = DetermineEncoding(readOnlySequence.FirstSpan, postResponse.Content.Headers);

        var maxTotalChars = encoding.GetMaxCharCount(totalBytes);
        
        var charsBuffer = ArrayPool<char>.Shared.Rent(maxTotalChars);
        
        if (offset != 0)
            readOnlySequence = readOnlySequence.Slice(offset);
        
        int writtenChars = encoding.GetChars(readOnlySequence, charsBuffer.AsSpan());
        return new Lease<char>(charsBuffer, writtenChars);
    }

    static (Encoding encoding, int offset) DetermineEncoding(ReadOnlySpan<byte> buffer, HttpContentHeaders headers)
    {
        // We don't validate the Content-Encoding header: If the content was encoded, it's the caller's
        // responsibility to make sure to only call ReadAsString() on already decoded content. E.g. if the
        // Content-Encoding is 'gzip' the user should set HttpClientHandler.AutomaticDecompression to get a
        // decoded response stream.

        Encoding? encoding = null;
        int bomLength = -1;

        string? charset = headers.ContentType?.CharSet;

        // If we do have encoding information in the 'Content-Type' header, use that information to convert
        // the content to a string.
        if (charset != null)
        {
            try
            {
                // Remove at most a single set of quotes.
                if (charset.Length > 2 && charset.StartsWith('\"') && charset.EndsWith('\"'))
                {
                    encoding = Encoding.GetEncoding(charset.Substring(1, charset.Length - 2));
                }
                else
                {
                    encoding = Encoding.GetEncoding(charset);
                }

                // Byte-order-mark (BOM) characters may be present even if a charset was specified.
                bomLength = GetPreambleLength(buffer, encoding);
            }
            catch (ArgumentException e)
            {
                throw new InvalidOperationException("Invalid charset", e);
            }
        }

        // If no content encoding is listed in the ContentType HTTP header, or no Content-Type header present,
        // then check for a BOM in the data to figure out the encoding.
        if (encoding == null)
        {
            if (!TryDetectEncoding(buffer, out encoding, out bomLength))
            {
                // Use the default encoding (UTF8) if we couldn't detect one.
                encoding = DefaultStringEncoding;

                // We already checked to see if the data had a UTF8 BOM in TryDetectEncoding
                // and DefaultStringEncoding is UTF8, so the bomLength is 0.
                bomLength = 0;
            }
        }

        return (encoding, bomLength);
    }
    
    private static bool TryDetectEncoding(ReadOnlySpan<byte> buffer, [NotNullWhen(true)] out Encoding? encoding, out int preambleLength)
    {
        var data = buffer;
        const int offset = 0;
        long dataLength = buffer.Length;

        if (dataLength >= 2)
        {
            int first2Bytes = data[offset + 0] << 8 | data[offset + 1];

            switch (first2Bytes)
            {
                case UTF8PreambleFirst2Bytes:
                    if (dataLength >= UTF8PreambleLength && data[offset + 2] == UTF8PreambleByte2)
                    {
                        encoding = Encoding.UTF8;
                        preambleLength = UTF8PreambleLength;
                        return true;
                    }
                    break;

                case UTF32OrUnicodePreambleFirst2Bytes:
                    // UTF32 not supported on Phone
                    if (dataLength >= UTF32PreambleLength && data[offset + 2] == UTF32PreambleByte2 && data[offset + 3] == UTF32PreambleByte3)
                    {
                        encoding = Encoding.UTF32;
                        preambleLength = UTF32PreambleLength;
                    }
                    else
                    {
                        encoding = Encoding.Unicode;
                        preambleLength = UnicodePreambleLength;
                    }
                    return true;

                case BigEndianUnicodePreambleFirst2Bytes:
                    encoding = Encoding.BigEndianUnicode;
                    preambleLength = BigEndianUnicodePreambleLength;
                    return true;
            }
        }

        encoding = null;
        preambleLength = 0;
        return false;
    }

    private static int GetPreambleLength(ReadOnlySpan<byte> buffer, Encoding encoding)
    {
        var data = buffer;
        const int offset = 0;
        long dataLength = buffer.Length;

        switch (encoding.CodePage)
        {
            case UTF8CodePage:
                return (dataLength >= UTF8PreambleLength
                        && data[offset + 0] == UTF8PreambleByte0
                        && data[offset + 1] == UTF8PreambleByte1
                        && data[offset + 2] == UTF8PreambleByte2) ? UTF8PreambleLength : 0;
            case UTF32CodePage:
                return (dataLength >= UTF32PreambleLength
                        && data[offset + 0] == UTF32PreambleByte0
                        && data[offset + 1] == UTF32PreambleByte1
                        && data[offset + 2] == UTF32PreambleByte2
                        && data[offset + 3] == UTF32PreambleByte3) ? UTF32PreambleLength : 0;
            case UnicodeCodePage:
                return (dataLength >= UnicodePreambleLength
                        && data[offset + 0] == UnicodePreambleByte0
                        && data[offset + 1] == UnicodePreambleByte1) ? UnicodePreambleLength : 0;

            case BigEndianUnicodeCodePage:
                return (dataLength >= BigEndianUnicodePreambleLength
                        && data[offset + 0] == BigEndianUnicodePreambleByte0
                        && data[offset + 1] == BigEndianUnicodePreambleByte1) ? BigEndianUnicodePreambleLength : 0;

            default:
                byte[] preamble = encoding.GetPreamble();
                return data.StartsWith(preamble) ? preamble.Length : 0;
        }
    }
}