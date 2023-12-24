using AngleSharp.Common;
using AngleSharp.Dom;

namespace AngleSharp.ReadOnlyDom.ReadOnly.Html.Model;

internal class ReadOnlyTextNode : ReadOnlyCharacterData, IReadOnlyTextNode
{
    public ReadOnlyTextNode(ReadOnlyDocument? owner, StringOrMemory content)
        :  base(owner, "#text", NodeType.Text, content)
    {
    }

    public override void Print(TextWriter writer)
    {
        writer.Write(Content.Memory.Span);
    }
}