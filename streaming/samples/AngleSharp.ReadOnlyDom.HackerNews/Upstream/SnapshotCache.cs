using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Net.Http.Headers;

namespace AngleSharp.ReadOnlyDom.HackerNews.Upstream;

/// <summary>A stored fold, with the validator computed once rather than per request that reads it.</summary>
internal sealed record NdjsonSnapshot(ReadOnlyMemory<byte> Ndjson, EntityTagHeaderValue ETag, long Timestamp);

/// <summary>
/// Keeps the NDJSON fold of a feed or a preview for as long as the caller says it stays fresh, so a page
/// that refreshes, several open tabs, and a scroll back up the list cost the upstream site one request
/// rather than one each. Eviction is deliberately crude: past a hard entry count the whole table is dropped.
/// </summary>
internal sealed class SnapshotCache(int maximumEntries = 512)
{
    private readonly ConcurrentDictionary<string, NdjsonSnapshot> _snapshots = new(StringComparer.Ordinal);

    internal bool TryGet(string key, TimeSpan lifetime, out NdjsonSnapshot snapshot, out TimeSpan age)
    {
        if (_snapshots.TryGetValue(key, out var stored))
        {
            age = Stopwatch.GetElapsedTime(stored.Timestamp);
            if (age < lifetime)
            {
                snapshot = stored;
                return true;
            }
        }

        snapshot = null!;
        age = default;
        return false;
    }

    internal void Store(string key, ReadOnlyMemory<byte> ndjson)
    {
        if (_snapshots.Count >= maximumEntries)
            _snapshots.Clear();

        var body = ndjson.ToArray();
        _snapshots[key] = new NdjsonSnapshot(body, ComputeETag(body), Stopwatch.GetTimestamp());
    }

    /// <summary>A strong validator over the stored bytes. Hashing happens on the miss path, once.</summary>
    private static EntityTagHeaderValue ComputeETag(ReadOnlySpan<byte> body) =>
        new($"\"{Convert.ToHexStringLower(SHA256.HashData(body).AsSpan(0, 12))}\"");
}
