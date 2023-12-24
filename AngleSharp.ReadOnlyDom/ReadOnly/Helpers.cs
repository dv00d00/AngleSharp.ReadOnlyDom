using AngleSharp.Html.Parser.Tokens.Struct;

namespace AngleSharp.ReadOnly.Html.Model;

internal static class Helpers
{
    public static void SetUniqueAttributes(this ReadOnlyElement element, ref StructHtmlToken token)
    {
        for (var i = token.Attributes.Count - 1; i >= 0; i--)
        {
            if (element.HasAttribute(token.Attributes[i].Name.String))
            {
                token.RemoveAttributeAt(i);
            }
        }

        element.SetAttributes(token.Attributes);
    }
    
    public static void AddComment(this ReadOnlyElement parent, ref StructHtmlToken token)
    {
        parent.AddNode(token.IsProcessingInstruction
            ? ReadOnlyProcessingInstruction.Create(parent.Owner, token.Data.String)
            : new ReadOnlyComment(parent.Owner, token.Data.String));
    }

    public static void AddComment(this ReadOnlyDocument parent, ref StructHtmlToken token)
    {
        parent.AddNode(token.IsProcessingInstruction
            ? ReadOnlyProcessingInstruction.Create(parent, token.Data.String)
            : new ReadOnlyComment(parent, token.Data.String));
    }
}