using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;

namespace AngleSharp.ReadOnlyDom.Filters;

public struct FirstTagAndAllChildren(string tag)
{
    private int _depth = 0;
    private bool _started = false;

    public TokenConsumptionResult Loop(ref StructHtmlToken token, TokenConsumer next)
    {
        _started =
            _started || token.Type == HtmlTokenType.StartTag && token.Name.Memory.Span.SequenceEqual(tag.AsSpan());

        if (_started)
        {
            if (token is { Type: HtmlTokenType.StartTag, IsSelfClosing: false })
            {
                _depth++;
            }
            else if (token.Type == HtmlTokenType.EndTag)
            {
                _depth--;
            }

            if (_depth > 0)
            {
                next(ref token);
            }
            else
            {
                return TokenConsumptionResult.Stop;
            }
        }

        return TokenConsumptionResult.Continue;
    }
}
