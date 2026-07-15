namespace AngleSharp.ReadOnlyDom.Streaming;

internal interface IOptimizedUtf8HtmlTokenSink : IUtf8HtmlTokenSink
{
    void StartTag(ReadOnlySpan<byte> name, ulong hash);

    void EndTag(ReadOnlySpan<byte> name, ulong hash);

    bool WantsAttribute(ReadOnlySpan<byte> name);
}
