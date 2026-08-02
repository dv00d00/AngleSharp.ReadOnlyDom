namespace AngleSharp.ReadOnlyDom.Compact;

/// <summary>
/// How a selector step relates to the step before it.
/// </summary>
public enum CompactPathAxis
{
    /// <summary>Matches at any depth below the previous step.</summary>
    Descendant,

    /// <summary>Matches only as a direct child of the previous step.</summary>
    Child,
}

/// <summary>
/// Whether an extracted value points into document-owned storage or carries its own copy.
/// </summary>
public enum CompactValueOwnership
{
    None,
    BorrowedDocumentSlice,
    Owned,
}

/// <summary>
/// A single extracted value. Attribute values taken from a materialized document are borrowed
/// slices of that document's text storage; values produced at construction time, and any normalized
/// subtree text, are owned copies that outlive the arena.
/// </summary>
public readonly struct CompactExtractionValue
{
    private readonly CompactDocument? _document;
    private readonly int _start;
    private readonly int _length;
    private readonly string? _owned;

    internal CompactExtractionValue(CompactDocument document, int start, int length)
    {
        _document = document;
        _start = start;
        _length = length;
        _owned = null;
        Exists = true;
        Ownership = CompactValueOwnership.BorrowedDocumentSlice;
    }

    internal CompactExtractionValue(string owned)
    {
        _document = null;
        _start = 0;
        _length = owned.Length;
        _owned = owned;
        Exists = true;
        Ownership = CompactValueOwnership.Owned;
    }

    public bool Exists { get; }
    public CompactValueOwnership Ownership { get; }
    public ReadOnlySpan<char> Span =>
        !Exists ? default
        : _owned is not null ? _owned.AsSpan()
        : _document!.GetValue(_start, _length);

    public string Own() => Exists ? (_owned ?? Span.ToString()) : string.Empty;

    public override string ToString() => Own();
}
