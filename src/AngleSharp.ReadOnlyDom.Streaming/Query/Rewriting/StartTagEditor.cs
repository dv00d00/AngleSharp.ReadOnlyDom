namespace AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;

/// <summary>Records byte-preserving edits to the currently matched start tag.</summary>
public ref struct StartTagEditor
{
    private readonly IStartTagEditCollector _collector;
    private readonly long _sourceStart;
    private readonly long _sourceEnd;
    private readonly bool _selfClosing;

    internal StartTagEditor(IStartTagEditCollector collector, long sourceStart, long sourceEnd, bool selfClosing)
    {
        _collector = collector;
        _sourceStart = sourceStart;
        _sourceEnd = sourceEnd;
        _selfClosing = selfClosing;
    }

    /// <summary>
    /// Appends an attribute immediately before the tag close. The caller is responsible for choosing a name that is
    /// not already present on the element.
    /// </summary>
    public void AppendAttribute(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value) =>
        _collector.AppendAttribute(_sourceStart, _sourceEnd, _selfClosing, name, value);
}
