namespace AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;

internal sealed class HtmlTextMutation(long sourceStart, long sourceEnd)
{
    internal long SourceStart { get; } = sourceStart;
    internal long SourceEnd { get; } = sourceEnd;
    // Allocated on first use: a redaction pass mutates every matched chunk, so two eager lists per
    // chunk dominated the enabled lane's allocation profile.
    internal List<byte[]>? Before { get; set; }
    internal List<byte[]>? After { get; set; }
    internal byte[]? Replacement { get; set; }
    internal bool Removed { get; set; }
    internal int Sequence { get; set; }
}
