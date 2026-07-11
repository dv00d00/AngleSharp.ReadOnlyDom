using AngleSharp.Common;
using AngleSharp.Dom;

namespace AngleSharp.ReadOnlyDom.Html.Model;

// todo: wat?

internal class ReadOnlyCharacterData : ReadOnlyNode
{
    internal ReadOnlyCharacterData(ReadOnlyDocument? owner, StringOrMemory name, NodeType nodeType)
        : this(owner, name, nodeType, StringOrMemory.Empty) { }

    internal ReadOnlyCharacterData(
        ReadOnlyDocument? owner,
        StringOrMemory name,
        NodeType nodeType,
        StringOrMemory content
    )
        : base(owner, content, nodeType) { }

    public StringOrMemory Content => NodeName;
}
