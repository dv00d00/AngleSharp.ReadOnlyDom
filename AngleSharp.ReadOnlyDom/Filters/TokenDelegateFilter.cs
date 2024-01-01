using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;

namespace AngleSharp.ReadOnlyDom.Filters;

public delegate bool IsStartToken(ref StructHtmlToken token);

public struct TokenDelegateFilter
{
    private int _depth;
    private bool _started;
    private readonly IsStartToken _isStartTag;

    public TokenDelegateFilter(IsStartToken isStartTag)
    {
        _isStartTag = isStartTag;
        _depth = 0;
        _started = false;
    }

    public TokenConsumptionResult Loop(ref StructHtmlToken token, TokenConsumer next)
    {
        _started = _started || token.Type == HtmlTokenType.StartTag && _isStartTag(ref token);

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