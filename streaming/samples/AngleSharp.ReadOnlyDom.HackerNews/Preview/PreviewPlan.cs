using AngleSharp.ReadOnlyDom.Streaming.Query;

namespace AngleSharp.ReadOnlyDom.HackerNews.Preview;

/// <summary>
/// Reads the head of a linked page and nothing else. The <c>meta</c> and
/// <c>link</c> keys are matched in the callback rather than by one query node per key: a single node with
/// three projected attributes covers a dozen spellings of the same four fields, and adding another costs a
/// branch instead of a node.
/// </summary>
internal static class PreviewPlan
{
    internal static readonly QueryPlan<PreviewBuffer> Instance = Create();

    private static QueryPlan<PreviewBuffer> Create()
    {
        var html = StreamQuery.For<PreviewBuffer>("html").OnEnd(static (ref output) => output.Complete());

        html.Descendant("title").OnNormalizedText(static (ref o, in e) => o.DocumentTitle(e.TextUtf8));
        html.Descendant("base").OnClose(static (ref o, in e) => o.Base(in e), "href");
        html.Descendant("meta").OnClose(static (ref o, in e) => o.Meta(in e), "property", "name", "content");
        html.Descendant("link").OnClose(static (ref o, in e) => o.Link(in e), "rel", "href");

        // Two ways to learn the head is over: it closed, or the body started without it closing.
        html.Descendant("head").OnEnd(static (ref output) => output.CardComplete());
        html.Descendant("body").OnStart(static (ref output, in _) => output.CardComplete());

        return html.Compile();
    }
}
