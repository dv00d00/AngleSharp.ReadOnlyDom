using AngleSharp.Common;
using AngleSharp.Dom;

namespace AngleSharp.ReadOnlyDom.Html.Model;

internal class ReadOnlyComment(ReadOnlyDocument? owner, StringOrMemory tokenData)
    : ReadOnlyCharacterData(owner, "#comment", NodeType.Comment, tokenData),
        IReadOnlyCommentNode
{
    public override void Print(TextWriter writer)
    {
        writer.Write("<!--");
        writer.WriteSOM(Content);
        writer.Write("-->");
    }
}
