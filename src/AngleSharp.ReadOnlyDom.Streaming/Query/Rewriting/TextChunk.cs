namespace AngleSharp.ReadOnlyDom.Streaming.Query.Rewriting;

/// <summary>Identifies the tokenizer context of a raw text fragment.</summary>
public enum HtmlTextType : byte
{
    Data,
    RcData,
    RawText,
    ScriptData,
    PlainText,
    CDataSection,
}

/// <summary>
/// A callback-scoped fragment of the original UTF-8 text. Fragments are arbitrary and entities
/// are not decoded. The final fragment of a text node can be empty.
/// </summary>
public readonly ref struct TextChunk
{
    internal TextChunk(ReadOnlySpan<byte> utf8, HtmlTextType textType, bool isLastInTextNode)
    {
        Utf8 = utf8;
        TextType = textType;
        IsLastInTextNode = isLastInTextNode;
    }

    public ReadOnlySpan<byte> Utf8 { get; }

    public HtmlTextType TextType { get; }

    public bool IsLastInTextNode { get; }
}
