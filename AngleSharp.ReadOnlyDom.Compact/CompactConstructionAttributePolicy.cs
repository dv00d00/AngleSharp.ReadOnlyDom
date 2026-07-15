using AngleSharp.Html.Parser.Tokens.Struct;

namespace AngleSharp.ReadOnlyDom.Compact;

internal static class CompactConstructionAttributePolicy
{
    public static bool IsRequiredByTreeBuilder(ref StructHtmlToken token, ReadOnlySpan<char> attribute)
    {
        if (
            attribute.Equals("type", StringComparison.OrdinalIgnoreCase)
            || attribute.Equals("action", StringComparison.OrdinalIgnoreCase)
            || attribute.Equals("prompt", StringComparison.OrdinalIgnoreCase)
            || attribute.Equals("encoding", StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        // Reconstruction and the adoption agency algorithm compare complete attribute sets.
        var tag = token.Name.Memory.Span;
        return tag.Equals("a", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("b", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("big", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("code", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("em", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("font", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("i", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("nobr", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("s", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("small", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("strike", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("strong", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("tt", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("u", StringComparison.OrdinalIgnoreCase);
    }
}
