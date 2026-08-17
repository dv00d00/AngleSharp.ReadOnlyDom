using System.Collections.Concurrent;
using System.Diagnostics;

namespace AngleSharp.ReadOnlyDom.HackerNews.Upstream;

/// <summary>
/// Keeps the NDJSON fold of a feed or a preview for as long as the caller says it stays fresh. A page that
/// refreshes, several open tabs, and a scroll back up the list then cost the upstream site one request
/// rather than one each; the sample is a demonstration, not a reason to hammer someone else's server.
/// The eviction policy is a sample's policy: past a hard entry count the whole table is dropped.
/// </summary>
internal sealed class SnapshotCache(int maximumEntries = 512)
{
    private readonly ConcurrentDictionary<string, Snapshot> _snapshots = new(StringComparer.Ordinal);

    internal bool TryGet(string key, TimeSpan lifetime, out ReadOnlyMemory<byte> ndjson, out TimeSpan age)
    {
        if (_snapshots.TryGetValue(key, out var snapshot))
        {
            age = Stopwatch.GetElapsedTime(snapshot.Timestamp);
            if (age < lifetime)
            {
                ndjson = snapshot.Ndjson;
                return true;
            }
        }

        ndjson = default;
        age = default;
        return false;
    }

    internal void Store(string key, ReadOnlyMemory<byte> ndjson)
    {
        if (_snapshots.Count >= maximumEntries)
            _snapshots.Clear();

        _snapshots[key] = new Snapshot(ndjson.ToArray(), Stopwatch.GetTimestamp());
    }

    private readonly record struct Snapshot(ReadOnlyMemory<byte> Ndjson, long Timestamp);
}
