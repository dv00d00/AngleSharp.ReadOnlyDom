using System.Runtime.CompilerServices;
using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;
using AngleSharp.Html.Parser.Tokens.Struct;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal readonly struct ArenaHandle : IHtmlTreeConstructionNode<ArenaHandle>
{
    private static readonly ushort TemplateNameId = GetKnownNameId(TagNames.Template);
    private static readonly ushort FormNameId = GetKnownNameId(TagNames.Form);
    private static readonly ushort ScriptNameId = GetKnownNameId(TagNames.Script);
    private readonly Arena? _arena;

    public ArenaHandle(Arena arena, int handle)
    {
        _arena = arena;
        Value = handle;
    }

    internal Arena Arena => _arena!;
    internal int Value { get; }

    public bool IsNull => _arena is null;
    public bool IsTemplate => IsHtml(TemplateNameId);
    public bool IsForm => IsHtml(FormNameId);
    public bool IsScript => IsHtml(ScriptNameId);
    public StringOrMemory NodeName => Arena.Name(Value);
    public StringOrMemory LocalName => Arena.LocalName(Value);
    public StringOrMemory Prefix => Arena.Prefix(Value);
    public StringOrMemory NamespaceUri => Arena.NamespaceUri(Value);
    public NodeFlags Flags => Arena.Flags(Value);

    public ArenaHandle Parent =>
        Arena.Parent(Value) is var parent && parent >= 0 ? new ArenaHandle(Arena, parent) : default;

    public int ChildCount => Arena.ChildCount(Value);
    public IElement? AsDomElement => null;

    public ArenaHandle ChildAt(int index)
    {
        return new ArenaHandle(Arena, Arena.ChildAt(Value, index));
    }

    public void ClearChildren()
    {
        Arena.ClearChildren(Value);
    }

    public void RemoveFromParent()
    {
        Arena.RemoveFromParent(Value);
    }

    public void RemoveChild(ArenaHandle child)
    {
        Arena.RemoveChild(Value, child.Value);
    }

    public void RemoveNode(int index, ArenaHandle child)
    {
        if (Arena.ChildAt(Value, index) != child.Value)
            throw new ArgumentException(
                "The supplied node does not match the child at the requested index.",
                nameof(child)
            );
        Arena.RemoveChild(Value, child.Value);
    }

    public void InsertNode(int index, ArenaHandle child)
    {
        Arena.AddChild(Value, child.Value, index);
    }

    public void AddNode(ArenaHandle child)
    {
        Arena.AddChild(Value, child.Value);
    }

    public void AppendText(StringOrMemory text, bool emitWhiteSpaceOnly = false)
    {
        Arena.AddText(Value, text, emitWhiteSpaceOnly);
    }

    public void InsertText(int index, StringOrMemory text, bool emitWhiteSpaceOnly = false)
    {
        Arena.AddText(Value, text, emitWhiteSpaceOnly, index);
    }

    public void AddComment(ref StructHtmlToken token)
    {
        Arena.AddComment(Value, ref token);
    }

    public StringOrMemory GetAttribute(StringOrMemory namespaceUri, StringOrMemory localName)
    {
        return Arena.GetAttributeValue(Value, localName);
    }

    public bool HasAttribute(StringOrMemory name)
    {
        return Arena.HasAttribute(Value, name);
    }

    public void SetAttribute(string? namespaceUri, StringOrMemory name, StringOrMemory value)
    {
        Arena.SetOwnAttribute(Value, name, value);
    }

    public void SetOwnAttribute(StringOrMemory name, StringOrMemory value)
    {
        Arena.SetOwnAttribute(Value, name, value);
    }

    public void SetAttributes(in StructAttributes attributes)
    {
        for (var i = 0; i < attributes.Count; i++)
            Arena.SetOwnAttribute(Value, attributes[i].Name, attributes[i].Value);
        Arena.CompleteAttributes(Value);
    }

    public bool AttributesSame(ArenaHandle other)
    {
        return Arena.AttributesSame(Value, other.Value);
    }

    public void SetupElement() { }

    public ArenaHandle ShallowCopy()
    {
        var copy = Arena.CreateElementHandle(LocalName, Prefix, Flags);
        Arena.CopyAttributes(Value, copy);
        return new ArenaHandle(Arena, copy);
    }

    public void SetSourceReference(ISourceReference sourceReference)
    {
        Arena.SetSourceReference(Value, sourceReference);
    }

    public void PopulateFragment()
    {
        Arena.PopulateTemplate(Value);
    }

    public void HandleMeta() { }

    public bool PrepareScript(IConstructableDocumentState document)
    {
        return false;
    }

    public Task RunScriptAsync(CancellationToken cancel)
    {
        return Task.CompletedTask;
    }

    public bool Equals(ArenaHandle other)
    {
        return ReferenceEquals(_arena, other._arena) && Value == other.Value;
    }

    public override bool Equals(object? obj)
    {
        return obj is ArenaHandle other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(_arena is null ? 0 : RuntimeHelpers.GetHashCode(_arena), Value);
    }

    private bool IsHtml(ushort nameId)
    {
        return (Arena.Flags(Value) & NodeFlags.HtmlMember) != 0 && Arena.NameId(Value) == nameId;
    }

    private static ushort GetKnownNameId(StringOrMemory name)
    {
        return GeneratedTagMetadata.TryGetKnownNameId(name, out var id)
            ? id
            : throw new InvalidOperationException($"Expected a generated name ID for '{name}'.");
    }
}
