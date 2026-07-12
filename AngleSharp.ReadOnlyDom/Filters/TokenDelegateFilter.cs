using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;

namespace AngleSharp.ReadOnlyDom.Filters;

internal static class SubtreeTokenDepth
{
    public static bool OpensScope(ref StructHtmlToken token) =>
        token.Type == HtmlTokenType.StartTag
        && !token.IsSelfClosing
        && (GeneratedTagMetadata.GetFlags(token.Name) & AngleSharp.Dom.NodeFlags.SelfClosing) == 0;
}

public delegate bool IsStartToken(ref StructHtmlToken token);

public struct TokenDelegateFilter(IsStartToken isStartTag)
{
    private int _depth = 0;
    private bool _started = false;

    public TokenConsumptionResult Loop(ref StructHtmlToken token, TokenConsumer next)
    {
        _started = _started || token.Type == HtmlTokenType.StartTag && isStartTag(ref token);

        if (_started)
        {
            if (SubtreeTokenDepth.OpensScope(ref token))
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
