using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.ReadOnly.Html.Model;

class ReadOnlyHtmlElement : ReadOnlyElement, IConstructableElement
{
    public ReadOnlyHtmlElement(ReadOnlyDocument? owner, StringOrMemory localName = default, StringOrMemory prefix = default, NodeFlags flags = NodeFlags.None)
        : base(owner, Combine(prefix, localName), localName, prefix, NamespaceNames.HtmlUri, flags | NodeFlags.HtmlMember)
    {
    }

    protected ReadOnlyHtmlElement(
            ReadOnlyDocument? owner,
            StringOrMemory name,
            StringOrMemory localName,
            StringOrMemory prefix,
            StringOrMemory namespaceUri,
            NodeFlags flags) :
        base(owner, name, localName, prefix, namespaceUri, flags)
    {
    }

    protected static StringOrMemory Combine(StringOrMemory prefix, StringOrMemory localName)
    {
        if (prefix.IsNullOrEmpty)
        {
            return localName;
        }
        
        return string.Concat(prefix.Memory.Span, ":", localName.Memory.Span);
    }

    public StringOrMemory Prefix => StringOrMemory.Empty;

    public void SetOwnAttribute(StringOrMemory name, StringOrMemory value)
    {
        _attributes ??= new ReadOnlyNamedNodeMap();
        _attributes.AddOrUpdate(name, value);
    }

    public void SetupElement() { }

    public virtual IConstructableNode ShallowCopy()
    {
        var readOnlyElement = new ReadOnlyHtmlElement(Owner, LocalName, prefix: default, Flags);
        PopulateAttributes(readOnlyElement);
        return readOnlyElement;
    }
    
}