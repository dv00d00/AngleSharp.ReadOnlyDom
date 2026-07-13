using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;
using AngleSharp.Html.Parser.Tokens.Struct;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal class ArenaElement : ArenaNode, IConstructableElement
{
    private ArenaNamedNodeMap? _attributes;

    public ArenaElement(Arena arena, int handle)
        : base(arena, handle) { }

    public StringOrMemory NamespaceUri => Arena.NamespaceUri(NodeHandle);
    public StringOrMemory LocalName => Arena.LocalName(NodeHandle);
    public StringOrMemory Prefix => Arena.Prefix(NodeHandle);
    public IConstructableNamedNodeMap Attributes => _attributes ??= new ArenaNamedNodeMap(Arena, NodeHandle);
    public ISourceReference? SourceReference
    {
        get => Arena.SourceReference(NodeHandle);
        set => Arena.SetSourceReference(NodeHandle, value);
    }

    public void SetAttribute(string? _, StringOrMemory name, StringOrMemory value) => SetOwnAttribute(name, value);

    public void SetOwnAttribute(StringOrMemory name, StringOrMemory value) =>
        Arena.SetOwnAttribute(NodeHandle, name, value);

    public StringOrMemory GetAttribute(StringOrMemory _, StringOrMemory name)
    {
        return Arena.GetAttribute(NodeHandle, name)?.Value ?? StringOrMemory.Empty;
    }

    public void SetAttributes(StructAttributes attributes)
    {
        for (var i = 0; i < attributes.Count; i++)
            SetOwnAttribute(attributes[i].Name, attributes[i].Value);
        Arena.CompleteAttributes(NodeHandle);
    }

    public bool HasAttribute(StringOrMemory name) => Arena.GetAttribute(NodeHandle, name) is not null;

    public void SetupElement() { }

    public virtual IConstructableNode ShallowCopy()
    {
        var copy = Arena.CreateElement(LocalName, Prefix, NamespaceUri, Flags);
        Arena.CopyAttributes(NodeHandle, copy.NodeHandle);
        return copy;
    }
}
