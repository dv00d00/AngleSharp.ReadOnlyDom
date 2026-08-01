using System.Runtime.CompilerServices;
using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;
using AngleSharp.Html.Parser.Tokens.Struct;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal readonly struct ArenaHandle : IHtmlTreeConstructionNode<ArenaHandle>
{
    private readonly Arena? _arena;
    private readonly int _handle;

    public ArenaHandle(Arena arena, int handle)
    {
        _arena = arena;
        _handle = handle;
    }

    internal Arena Arena => _arena!;
    internal int Value => _handle;
    public bool IsNull => _arena is null;
    public bool IsTemplate => IsHtml(TagNames.Template);
    public bool IsForm => IsHtml(TagNames.Form);
    public bool IsScript => IsHtml(TagNames.Script);
    public StringOrMemory NodeName => Arena.Name(_handle);
    public StringOrMemory LocalName => Arena.LocalName(_handle);
    public StringOrMemory Prefix => Arena.Prefix(_handle);
    public StringOrMemory NamespaceUri => Arena.NamespaceUri(_handle);
    public NodeFlags Flags => Arena.Flags(_handle);
    public ArenaHandle Parent => Arena.Parent(_handle) is var parent && parent >= 0 ? new(Arena, parent) : default;
    public int ChildCount => Arena.ChildCount(_handle);
    public IElement? AsDomElement => null;

    public ArenaHandle ChildAt(int index) => new(Arena, Arena.ChildAt(_handle, index));

    public void ClearChildren() => Arena.ClearChildren(_handle);

    public void RemoveFromParent() => Arena.RemoveFromParent(_handle);

    public void RemoveChild(ArenaHandle child) => Arena.RemoveChild(_handle, child._handle);

    public void RemoveNode(int index, ArenaHandle child)
    {
        if (Arena.ChildAt(_handle, index) != child._handle)
            throw new ArgumentException("The supplied node does not match the child at the requested index.", nameof(child));
        Arena.RemoveChild(_handle, child._handle);
    }

    public void InsertNode(int index, ArenaHandle child) => Arena.AddChild(_handle, child._handle, index);

    public void AddNode(ArenaHandle child) => Arena.AddChild(_handle, child._handle);

    public void AppendText(StringOrMemory text, bool emitWhiteSpaceOnly = false) =>
        Arena.AddText(_handle, text, emitWhiteSpaceOnly);

    public void InsertText(int index, StringOrMemory text, bool emitWhiteSpaceOnly = false) =>
        Arena.AddText(_handle, text, emitWhiteSpaceOnly, index);

    public void AddComment(ref StructHtmlToken token) => Arena.AddComment(_handle, ref token);

    public StringOrMemory GetAttribute(StringOrMemory namespaceUri, StringOrMemory localName) =>
        Arena.GetAttributeValue(_handle, localName);

    public bool HasAttribute(StringOrMemory name) => Arena.HasAttribute(_handle, name);

    public void SetAttribute(string? namespaceUri, StringOrMemory name, StringOrMemory value) =>
        Arena.SetOwnAttribute(_handle, name, value);

    public void SetOwnAttribute(StringOrMemory name, StringOrMemory value) =>
        Arena.SetOwnAttribute(_handle, name, value);

    public void SetAttributes(in StructAttributes attributes)
    {
        for (var i = 0; i < attributes.Count; i++)
            Arena.SetOwnAttribute(_handle, attributes[i].Name, attributes[i].Value);
        Arena.CompleteAttributes(_handle);
    }

    public bool AttributesSame(ArenaHandle other) => Arena.AttributesSame(_handle, other._handle);

    public void SetupElement() { }

    public ArenaHandle ShallowCopy()
    {
        var copy = Arena.CreateElementHandle(LocalName, Prefix, Flags);
        Arena.CopyAttributes(_handle, copy);
        return new ArenaHandle(Arena, copy);
    }

    public void SetSourceReference(ISourceReference sourceReference) => Arena.SetSourceReference(_handle, sourceReference);

    public void PopulateFragment() => Arena.PopulateTemplate(_handle);

    public void HandleMeta() { }

    public bool PrepareScript(IConstructableDocument document) => false;

    public Task RunScriptAsync(CancellationToken cancel) => Task.CompletedTask;

    public bool Equals(ArenaHandle other) => ReferenceEquals(_arena, other._arena) && _handle == other._handle;

    public override bool Equals(object? obj) => obj is ArenaHandle other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_arena is null ? 0 : RuntimeHelpers.GetHashCode(_arena), _handle);

    private bool IsHtml(StringOrMemory name) => Flags.HasFlag(NodeFlags.HtmlMember) && LocalName.Equals(name);
}
