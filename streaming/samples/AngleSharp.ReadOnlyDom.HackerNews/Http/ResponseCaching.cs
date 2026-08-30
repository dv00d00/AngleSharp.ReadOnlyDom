using System.Globalization;
using AngleSharp.ReadOnlyDom.HackerNews.Upstream;
using Microsoft.Net.Http.Headers;

namespace AngleSharp.ReadOnlyDom.HackerNews.Http;

/// <summary>
/// Cache headers for the NDJSON endpoints, built from the framework's typed headers and result helpers:
/// <c>Results.Bytes</c> handles the conditional request, so If-None-Match becomes a 304 without a tag
/// comparison written here.
/// <para>
/// A response is only ever as fresh as the snapshot it came from, so <c>max-age</c> is the snapshot lifetime
/// and <c>Age</c> is how much of it is already spent: a client copy expires when the server's does, instead
/// of stacking one lifetime on top of the other.
/// </para>
/// </summary>
internal static class ResponseCaching
{
    /// <summary>Serves a stored snapshot; the framework answers 304 when the client already holds it.</summary>
    internal static IResult Snapshot(
        HttpContext context,
        NdjsonSnapshot snapshot,
        TimeSpan age,
        TimeSpan lifetime,
        string contentType
    )
    {
        Fresh(context.Response, lifetime);
        context.Response.Headers.Age = ((int)age.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        return Results.Bytes(snapshot.Ndjson, contentType, entityTag: snapshot.ETag);
    }

    /// <summary>Marks a live, still-streaming response cacheable for the lifetime of a snapshot of it.</summary>
    internal static void Fresh(HttpResponse response, TimeSpan lifetime) =>
        response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue { Private = true, MaxAge = lifetime };

    internal static void NoStore(HttpResponse response) =>
        response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue { NoStore = true };
}
