namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

#pragma warning disable CS1591
internal interface IUtf8HtmlTokenizer
{
    Utf8HtmlTokenizerCounters Counters { get; }

    void Write(ReadOnlyMemory<Byte> utf8);

    void Write(ReadOnlySpan<Byte> utf8);

    void Complete();
}