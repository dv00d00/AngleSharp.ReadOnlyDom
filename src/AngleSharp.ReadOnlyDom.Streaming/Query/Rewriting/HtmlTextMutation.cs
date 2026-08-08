namespace AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;

internal sealed class HtmlTextMutation(long sourceStart, long sourceEnd)
{
    internal long SourceStart { get; } = sourceStart;
    internal long SourceEnd { get; } = sourceEnd;
    internal List<byte[]> Before { get; } = [];
    internal List<byte[]> After { get; } = [];
    internal byte[]? Replacement { get; set; }
    internal bool Removed { get; set; }
    internal int Sequence { get; set; }
}
