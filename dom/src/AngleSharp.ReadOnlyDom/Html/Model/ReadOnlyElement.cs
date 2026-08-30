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
        ReadOnlyDocument
    > SourceOwners = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        ReadOnlyElement,
        LegacySourceReference
    > LegacySources = new();

    protected ReadOnlyNamedNodeMap? _attributes;

    public StringOrMemory LocalName
    {
        get
        {
            var separator = NodeName.Memory.Span.IndexOf(':');
            return separator < 0 ? NodeName : NodeName.Memory.Slice(separator + 1);
        }
    }

    public StringOrMemory NamespaceUri =>
        (Flags & NodeFlags.SvgMember) != 0 ? NamespaceNames.SvgUri
        : (Flags & NodeFlags.MathMember) != 0 ? NamespaceNames.MathMlUri
        : NamespaceNames.HtmlUri;

    public StringOrMemory Prefix
    {
        get
        {
            var separator = NodeName.Memory.Span.IndexOf(':');
            return separator < 0 ? default : NodeName.Memory.Slice(0, separator);
        }
    }

    public IConstructableNamedNodeMap Attributes => _attributes ?? EmptyAttributes;

    public ISourceReference? SourceReference
    {
        get =>
            SourceOwners.TryGetValue(this, out var owner) ? owner.GetSourceReference(this)
            : LegacySources.TryGetValue(this, out var legacy) ? legacy.Value
            : null;
        set
        {
            if (SourceOwners.TryGetValue(this, out var owner))
            {
                owner.SetSourceReference(this, value);
            }
            else if (value is null)
            {
                LegacySources.Remove(this);
            }
            else
            {
                LegacySources.GetOrCreateValue(this).Value = value;
            }
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
        : base(owner, name, flags)
    {
        if (owner?.TracksSources == true)
            SourceOwners.Add(this, owner);
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
            other._attributes = _attributes.Clone();
        }
    }

    private sealed class LegacySourceReference
    {
        public ISourceReference? Value;
    }
}
