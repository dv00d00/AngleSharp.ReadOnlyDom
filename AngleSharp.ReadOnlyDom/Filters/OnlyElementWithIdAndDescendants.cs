using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;

namespace AngleSharp.ReadOnlyDom.Filters;

public struct OnlyElementWithIdAndDescendants
{
    private readonly string _id;
    private readonly string _tag;

    private int _depth;
    private bool _started;

    public OnlyElementWithIdAndDescendants(string tag, string id)
    {
        _tag = tag;
        _id = id;
        _depth = 0;
        _started = false;
    }

    public TokenConsumptionResult Loop(ref StructHtmlToken token, TokenConsumer next)
    {
        _started = _started ||
                   token.Type == HtmlTokenType.StartTag &&
                   token.Name.Memory.Span.SequenceEqual(_tag.AsSpan()) &&
                   token.Attributes.HasAttribute(AttributeNames.Id, _id);

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