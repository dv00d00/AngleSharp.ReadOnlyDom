using AngleSharp.Common;

namespace AngleSharp.ReadOnlyDom.Html.Model;

internal class ReadOnlyTextNode(ReadOnlyDocument? owner, StringOrMemory content)
    : ReadOnlyCharacterData(owner, content),
        IReadOnlyTextNode
{
    public override StringOrMemory NodeName => "#text";

    public override void Print(TextWriter writer)
    {
        writer.WriteSOM(Content);
    }
}
