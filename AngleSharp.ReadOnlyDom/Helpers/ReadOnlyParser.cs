using AngleSharp.Html.Construction;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.ReadOnly;
using AngleSharp.ReadOnlyDom.ReadOnly.Html;
using AngleSharp.ReadOnlyDom.ReadOnly.Html.Model;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Helpers;

public static class ReadOnlyParser
{
    private static Func<IBrowsingContext, IDomConstructionElementFactory<ReadOnlyDocument, ReadOnlyHtmlElement>> _service = _ => new ReadOnlyDomConstructionFactory();
    public static readonly IConfiguration DefaultConfig = Configuration.Default.With(_service);
    public static readonly IBrowsingContext DefaultContext = BrowsingContext.New(DefaultConfig);

    public static IReadOnlyDocument ParseReadOnlyDocument(this IHtmlParser parser, TextSource source, TokenizerMiddleware? middleware = null)
    {
        return parser.ParseDocument<ReadOnlyDocument, ReadOnlyHtmlElement>(source, middleware);
    }
    
    public static IReadOnlyDocument ParseReadOnlyDocument(this IHtmlParser parser, char[] source, int length, TokenizerMiddleware? middleware = null)
    {
        return parser.ParseDocument<ReadOnlyDocument, ReadOnlyHtmlElement>(new TextSource(new CharArrayTextSource(source, length)), middleware);
    }
    
    public static IReadOnlyDocument ParseReadOnlyDocument(this IHtmlParser parser, string source, TokenizerMiddleware? middleware = null)
    {
        return parser.ParseDocument<ReadOnlyDocument, ReadOnlyHtmlElement>(new TextSource(new StringTextSource(source)), middleware);
    }
    
    public static IReadOnlyDocument ParseReadOnlyDocument(this IHtmlParser parser, ReadOnlyMemory<char> source, TokenizerMiddleware? middleware = null)
    {
        return parser.ParseDocument<ReadOnlyDocument, ReadOnlyHtmlElement>(new TextSource(new ReadOnlyMemoryTextSource(source)), middleware);
    }
}