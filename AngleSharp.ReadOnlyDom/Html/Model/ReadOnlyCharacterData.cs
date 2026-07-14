using AngleSharp.Common;

namespace AngleSharp.ReadOnlyDom.Html.Model;

internal class ReadOnlyCharacterData : ReadOnlyNode
{
    internal ReadOnlyCharacterData(ReadOnlyDocument? owner, StringOrMemory content)
        : base(owner, content) { }

    public StringOrMemory Content => _nodeName;
}
