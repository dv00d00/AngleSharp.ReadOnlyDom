namespace AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;

internal sealed class HtmlTextMutation(long sourceStart, long sourceEnd)
{
    internal long SourceStart { get; private set; } = sourceStart;
    internal long SourceEnd { get; private set; } = sourceEnd;

    // Allocated on first use: a redaction pass mutates every matched chunk, so two eager lists per
    // chunk dominated the enabled lane's allocation profile.
    internal List<byte[]>? Before { get; set; }
    internal List<byte[]>? After { get; set; }
    internal byte[]? Replacement { get; set; }
    internal bool Removed { get; set; }
    internal int Sequence { get; set; }

    internal HtmlTextMutation? NextPooled { get; set; }

    internal void Reset(long sourceStart, long sourceEnd)
    {
        SourceStart = sourceStart;
        SourceEnd = sourceEnd;
        Before = null;
        After = null;
        Replacement = null;
        Removed = false;
        Sequence = 0;
        NextPooled = null;
    }
}

internal static class HtmlTextMutationPool
{
    private const int MaximumRetainedPerThread = 64;

    [ThreadStatic]
    private static HtmlTextMutation? _first;

    [ThreadStatic]
    private static int _count;

    internal static HtmlTextMutation Rent(long sourceStart, long sourceEnd)
    {
        var mutation = _first;
        if (mutation is null)
            return new HtmlTextMutation(sourceStart, sourceEnd);

        _first = mutation.NextPooled;
        _count--;
        mutation.Reset(sourceStart, sourceEnd);
        return mutation;
    }

    internal static void Return(HtmlTextMutation mutation)
    {
        mutation.Reset(0, 0);
        if (_count >= MaximumRetainedPerThread)
            return;

        mutation.NextPooled = _first;
        _first = mutation;
        _count++;
    }
}
