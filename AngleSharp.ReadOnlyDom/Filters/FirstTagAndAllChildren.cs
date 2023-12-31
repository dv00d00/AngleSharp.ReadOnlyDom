using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;

namespace AngleSharp.ReadOnlyDom.Filters;

public struct FirstTagAndAllChildren
{
    private readonly string _tag;
    private int _depth;
    private bool _started;

    public FirstTagAndAllChildren(string tag)
    {
        _tag = tag;
        _depth = 0;
        _started = false;
    }

    public Result Loop(ref StructHtmlToken token, TokenConsumer next)
    {
        _started = _started ||
                   token.Type == HtmlTokenType.StartTag &&
                   token.Name.Memory.Span.SequenceEqual(_tag.AsSpan());

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
                return Result.Stop;
            }
        }

        return Result.Continue;
    }
}