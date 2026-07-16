namespace AngleSharp.ReadOnlyDom.Streaming;

/// <summary>
/// Synchronous borrowed views over tokenizer-owned or PipeReader-owned UTF-8. Every span is valid only for the duration
/// of its callback. The split start-tag callbacks let a construction sink collect only attributes it needs.
/// </summary>
public interface IUtf8HtmlTokenSink
{
    void Text(ReadOnlySpan<byte> utf8);

    void StartTag(ReadOnlySpan<byte> name);

    void Attribute(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value);

    void StartTagEnd(bool selfClosing);

    void EndTag(ReadOnlySpan<byte> name);

    void Comment(ReadOnlySpan<byte> utf8) { }

    void Doctype(ReadOnlySpan<byte> utf8) { }

    void Doctype(in Utf8DoctypeToken token) => Doctype(token.Name);

    void EndOfFile() { }
}
