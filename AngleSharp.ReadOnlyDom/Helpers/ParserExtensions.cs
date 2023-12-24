using AngleSharp.Html.Parser;
using AngleSharp.ReadOnly.Html;
using AngleSharp.Text;

namespace AngleSharp.Benchmarks.UserCode;

public static class ParserExtensions
{
    public static IReadOnlyDocument ParseReadOnlyDocument(this IHtmlParser parser, IReadOnlyTextSource source, TokenizerMiddleware? middleware = null)
    {
        return parser.ParseDocument<ReadOnlyDocument, ReadOnlyHtmlElement>(source, middleware);
    }
}