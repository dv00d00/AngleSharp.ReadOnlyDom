using AngleSharp.Common;
using AngleSharp.Dom;

namespace AngleSharp.ReadOnlyDom.Html.Model;

internal class ReadOnlyTextNode(ReadOnlyDocument? owner, StringOrMemory content)
    : ReadOnlyCharacterData(owner, "#text", NodeType.Text, content), IReadOnlyTextNode
{
    public override void Print(TextWriter writer)
    {
        writer.WriteSOM(Content);
    }
}