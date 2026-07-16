using AngleSharp.ReadOnlyDom.Streaming.Utf8Stream.Query;

internal static class MarkdownQueryExtensions
{
    internal static QueryNode<MarkdownBuffer> AsInlineBlock(
        this QueryNode<MarkdownBuffer> node,
        ReadOnlySpan<byte> prefix = default
    )
    {
        // Query plans live for the lifetime of the application; copy the compile-time span once.
        var ownedPrefix = prefix.ToArray();
        return node.OnStart((ref MarkdownBuffer output, in Element _) => output.StartInlineBlock(ownedPrefix))
            .OnEnd(static (ref output) => output.EndInlineBlock());
    }

    internal static QueryNode<MarkdownBuffer> AsInlineLink(this QueryNode<MarkdownBuffer> node) =>
        node.OnStart(
                static (ref output, in element) =>
                {
                    if (element.TryGetAttribute("href"u8, out var href))
                        output.StartInlineLink(href);
                },
                "href"
            )
            .OnEnd(static (ref output) => output.EndInlineLink());
}
