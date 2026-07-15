using AngleSharp.Common;

namespace AngleSharp.ReadOnlyDom.Html.Model;

internal class ReadOnlyComment(ReadOnlyDocument? owner, StringOrMemory tokenData)
    : ReadOnlyCharacterData(owner, tokenData),
        IReadOnlyCommentNode
{
    public override StringOrMemory NodeName => "#comment";

    public override void Print(TextWriter writer)
    {
        writer.Write("<!--");
        writer.WriteSOM(Content);
        writer.Write("-->");
    }
}
