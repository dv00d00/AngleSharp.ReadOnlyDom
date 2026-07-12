using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;
using AngleSharp.Html.Parser.Tokens.Struct;

namespace AngleSharp.ReadOnlyDom.Html.Model;

internal abstract class ReadOnlyElement : ReadOnlyNode, IReadOnlyElement
{
    private static readonly ReadOnlyNamedNodeMap EmptyAttributes = new ReadOnlyNamedNodeMap();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        ReadOnlyElement,
        OptionalMetadata
    > Metadata = new();

    protected ReadOnlyNamedNodeMap? _attributes;

    public StringOrMemory LocalName => NodeName;

    public StringOrMemory NamespaceUri =>
        (Flags & NodeFlags.SvgMember) != 0 ? NamespaceNames.SvgUri
        : (Flags & NodeFlags.MathMember) != 0 ? NamespaceNames.MathMlUri
        : NamespaceNames.HtmlUri;

    public StringOrMemory Prefix => Metadata.TryGetValue(this, out var metadata) ? metadata.Prefix : default;

    public IConstructableNamedNodeMap Attributes => _attributes ?? EmptyAttributes;

    public ISourceReference? SourceReference
    {
        get => Metadata.TryGetValue(this, out var metadata) ? metadata.SourceReference : null;
        set
        {
            if (value is null)
            {
                if (Metadata.TryGetValue(this, out var metadata))
                {
                    metadata.SourceReference = null;
                    RemoveMetadataIfEmpty(metadata);
                }

                return;
            }

            Metadata.GetOrCreateValue(this).SourceReference = value;
        }
    }

    /// <inheritdoc />
    public ReadOnlyElement(
        ReadOnlyDocument? owner,
        StringOrMemory name,
        StringOrMemory localName,
        StringOrMemory prefix,
        StringOrMemory namespaceUri,
        NodeFlags flags = NodeFlags.None
    )
        : base(owner, name, NodeType.Element, flags)
    {
        if (!prefix.IsNullOrEmpty)
        {
            Metadata.GetOrCreateValue(this).Prefix = prefix;
        }
    }

    public StringOrMemory GetAttribute(StringOrMemory @namespace, StringOrMemory name)
    {
        if (_attributes is null)
        {
            return StringOrMemory.Empty;
        }

        return _attributes[name]?.Value ?? StringOrMemory.Empty;
    }

    public bool HasAttribute(StringOrMemory name)
    {
        return _attributes?[name] != null;
    }

    public void SetAttribute(string? ns, StringOrMemory name, StringOrMemory value)
    {
        _attributes ??= new ReadOnlyNamedNodeMap();
        var attr = _attributes[name];
        if (attr is not null)
        {
            attr.Value = value;
        }
        else
        {
            _attributes.AddOrUpdate(name, value);
        }
    }

    public void SetAttributes(StructAttributes attributes)
    {
        if (attributes.Count == 0)
            return;

        _attributes ??= new ReadOnlyNamedNodeMap();
        for (int i = 0; i < attributes.Count; i++)
        {
            var attribute = attributes[i];
            SetAttribute(null, attribute.Name, attribute.Value);
        }
    }

    public override void Print(TextWriter writer)
    {
        writer.Write("<");
        writer.WriteSOM(NodeName);
        foreach (var attribute in _attributes ?? EmptyAttributes)
        {
            writer.Write(" ");
            writer.WriteSOM(attribute.Name);
            writer.Write("=\"");
            writer.WriteSOM(attribute.Value);
            writer.Write("\"");
        }
        writer.WriteLine(">");
        base.Print(writer);
        writer.Write("</");
        writer.WriteSOM(NodeName);
        writer.WriteLine(">");
    }

    IReadOnlyNamedNodeMap IReadOnlyElement.Attributes => _attributes ?? EmptyAttributes;

    protected void PopulateAttributes(ReadOnlyElement other)
    {
        if (_attributes != null)
        {
            // foreach (var attribute in _attributes)
            //     other.SetAttribute(null, attribute.Name, attribute.Value);
            other._attributes = _attributes;
        }
    }

    private void RemoveMetadataIfEmpty(OptionalMetadata metadata)
    {
        if (metadata.SourceReference is null && metadata.Prefix.IsNullOrEmpty)
        {
            Metadata.Remove(this);
        }
    }

    private sealed class OptionalMetadata
    {
        public StringOrMemory Prefix;
        public ISourceReference? SourceReference;
    }
}
