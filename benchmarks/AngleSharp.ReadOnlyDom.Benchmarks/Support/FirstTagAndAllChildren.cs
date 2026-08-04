using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;

namespace AngleSharp.ReadOnlyDom.Benchmarks.Support;

internal struct FirstTagAndAllChildren(string tag)
{
    private int _depth;
    private bool _started;

    public TokenConsumptionResult Loop(ref StructHtmlToken token, TokenConsumer next)
    {
        _started =
            _started || token.Type == HtmlTokenType.StartTag && token.Name.Memory.Span.SequenceEqual(tag.AsSpan());
        if (!_started)
            return TokenConsumptionResult.Continue;
        if (OpensScope(ref token))
            _depth++;
        else if (token.Type == HtmlTokenType.EndTag)
            _depth--;
        if (_depth <= 0)
            return TokenConsumptionResult.Stop;
        next(ref token);
        return TokenConsumptionResult.Continue;
    }

    private static bool OpensScope(ref StructHtmlToken token) =>
        token.Type == HtmlTokenType.StartTag && !IsHtmlVoid(token);

    private static bool IsHtmlVoid(StructHtmlToken token)
    {
        var name = token.Name;
        return name.Equals(TagNames.Hr)
            || name.Equals(TagNames.Br)
            || name.Equals(TagNames.Img)
            || name.Equals(TagNames.Col)
            || name.Equals(TagNames.Wbr)
            || name.Equals(TagNames.Area)
            || name.Equals(TagNames.Base)
            || name.Equals(TagNames.Link)
            || name.Equals(TagNames.Meta)
            || name.Equals(TagNames.Embed)
            || name.Equals(TagNames.Frame)
            || name.Equals(TagNames.Input)
            || name.Equals(TagNames.Param)
            || name.Equals(TagNames.Track)
            || name.Equals(TagNames.Keygen)
            || name.Equals(TagNames.Source)
            || name.Equals(TagNames.Bgsound)
            || name.Equals(TagNames.BaseFont)
            || name.Equals(TagNames.MenuItem);
    }
}
