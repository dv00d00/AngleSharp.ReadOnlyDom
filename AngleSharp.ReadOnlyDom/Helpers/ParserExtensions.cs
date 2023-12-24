using AngleSharp.Html.Construction;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.ReadOnly;
using AngleSharp.ReadOnlyDom.ReadOnly.Html;
using AngleSharp.ReadOnlyDom.ReadOnly.Html.Model;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Helpers;

public static class ParserExtensions
{
    private static Func<IBrowsingContext, IDomConstructionElementFactory<ReadOnlyDocument, ReadOnlyHtmlElement>> _service = _ => new ReadOnlyDomConstructionFactory();
    public static readonly IConfiguration DefaultConfig = Configuration.Default.With(_service);
    public static readonly IBrowsingContext DefaultContext = BrowsingContext.New(DefaultConfig);

    public static IReadOnlyDocument ParseReadOnlyDocument(this IHtmlParser parser, IReadOnlyTextSource source, TokenizerMiddleware? middleware = null)
    {
        return parser.ParseDocument<ReadOnlyDocument, ReadOnlyHtmlElement>(source, middleware);
    }
    
    public static IReadOnlyDocument ParseReadOnlyDocument(this IHtmlParser parser, char[] source, TokenizerMiddleware? middleware = null)
    {
        return parser.ParseDocument<ReadOnlyDocument, ReadOnlyHtmlElement>(source, middleware);
    }
    
    public static IReadOnlyDocument ParseReadOnlyDocument(this IHtmlParser parser, ReadOnlyMemory<char> source, TokenizerMiddleware? middleware = null)
    {
        return parser.ParseDocument<ReadOnlyDocument, ReadOnlyHtmlElement>(source, middleware);
    }
}