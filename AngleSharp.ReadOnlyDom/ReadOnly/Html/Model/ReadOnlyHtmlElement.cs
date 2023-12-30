using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.ReadOnly.Html.Model;

class ReadOnlySvgElement : ReadOnlyHtmlElement, IConstructableSvgElement
{
    public ReadOnlySvgElement(ReadOnlyDocument? owner, StringOrMemory localName = default, StringOrMemory prefix = default, NodeFlags flags = NodeFlags.None)
        : base(owner, Combine(prefix, localName), localName, prefix, NamespaceNames.SvgUri, flags | NodeFlags.SvgMember)
    {
    }

    public override IConstructableNode ShallowCopy()
    {
        var readOnlyElement = new ReadOnlySvgElement(Owner, LocalName, prefix: default, Flags);
        if (_attributes != null)
            foreach (var attribute in _attributes)
                readOnlyElement.SetOwnAttribute(attribute.Name, attribute.Value);
        return readOnlyElement;
    }
    
}

class ReadOnlyMathElement : ReadOnlyHtmlElement, IConstructableMathElement
{
    public ReadOnlyMathElement(ReadOnlyDocument? owner, StringOrMemory localName = default, StringOrMemory prefix = default, NodeFlags flags = NodeFlags.None)
        : base(owner, Combine(prefix, localName), localName, prefix, NamespaceNames.MathMlUri, flags | NodeFlags.MathMember)
    {
    }

    public override IConstructableNode ShallowCopy()
    {
        var readOnlyElement = new ReadOnlySvgElement(Owner, LocalName, prefix: default, Flags);
        if (_attributes != null)
            foreach (var attribute in _attributes)
                readOnlyElement.SetOwnAttribute(attribute.Name, attribute.Value);
        return readOnlyElement;
    }
}


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
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP2_1_OR_GREATER
        return string.Concat(prefix.Memory.Span, ":", localName.Memory.Span);
#else
        return String.Concat(prefix.String, ":", localName.String);
#endif
    }

    public StringOrMemory Prefix => StringOrMemory.Empty;

    public void SetOwnAttribute(StringOrMemory name, StringOrMemory value)
    {
        _attributes ??= new ReadOnlyNamedNodeMap();
        _attributes.AddOrUpdate(name, value);
    }

    public void SetupElement()
    {
    }

    public virtual IConstructableNode ShallowCopy()
    {
        var readOnlyElement = new ReadOnlyHtmlElement(Owner, LocalName, prefix: default, Flags);
        if (_attributes != null)
            foreach (var attribute in _attributes)
                readOnlyElement.SetOwnAttribute(attribute.Name, attribute.Value);
        return readOnlyElement;
    }
}