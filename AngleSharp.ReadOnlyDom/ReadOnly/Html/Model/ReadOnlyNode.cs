using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;
using AngleSharp.Html.Parser.Tokens.Struct;

namespace AngleSharp.ReadOnlyDom.ReadOnly.Html.Model;

internal abstract class ReadOnlyNode : IConstructableNode, IReadOnlyNode, IPrintable
{
    private static readonly ReadOnlyNodeList EmptyChildNodes = new ReadOnlyNodeList();
    private static ReadOnlySpan<char> WhiteSpace => " \t\r\n".AsSpan();
    
    protected readonly NodeFlags _flags;
    protected ReadOnlyNodeList? _childNodes;
    protected IConstructableNode? _parent;
    protected StringOrMemory _nodeName;

    public NodeFlags Flags => _flags;
    protected ReadOnlyNodeList _ChildNodes => _childNodes ?? EmptyChildNodes;
    IReadOnlyNode? IReadOnlyNode.Parent => _parent as IReadOnlyNode;
    IReadOnlyNodeList IReadOnlyNode.ChildNodes => _ChildNodes;

    public StringOrMemory NodeName { get => _nodeName; internal set => _nodeName = value; }
    public ReadOnlyDocument? Owner => null;

    public ReadOnlyNode(ReadOnlyDocument? owner, StringOrMemory name, NodeType nodeType = NodeType.Element, NodeFlags flags = NodeFlags.None)
    {
        _nodeName = name;
        _flags = flags;
    }

    public IConstructableNode? Parent
    {
        get => _parent;
        set => _parent = value;
    }

    public IConstructableNodeList ChildNodes => _ChildNodes;

    public void RemoveFromParent()
    {
        Parent?.RemoveChild(this);
    }

    public void RemoveChild(IConstructableNode childNode)
    {
        childNode.Parent = null;
        _childNodes?.Remove(childNode);
    }

    public void RemoveNode(int idx, IConstructableNode childNode)
    {
        childNode.Parent = null;
        _childNodes?.RemoveAt(idx);
    }

    public void InsertNode(int idx, IConstructableNode childNode)
    {
        childNode.Parent = this;
        _childNodes ??= new ReadOnlyNodeList();
        _childNodes?.Insert(idx, childNode);
    }

    public void AddNode(IConstructableNode childNode)
    {
        childNode.Parent = this;
        _childNodes ??= new ReadOnlyNodeList();
        _childNodes.Add(childNode);
    }

    public void AppendText(StringOrMemory text, bool emitWhiteSpaceOnly = false)
    {
        if (!emitWhiteSpaceOnly && text.Memory.Span.Trim(WhiteSpace).Length == 0)
        {
            return;
        }
        
        AddNode(new ReadOnlyTextNode(Owner, text));
    }

    public void InsertText(int idx, StringOrMemory text, bool emitWhiteSpaceOnly = false)
    {
        if (!emitWhiteSpaceOnly && text.Memory.Span.Trim(WhiteSpace).Length == 0)
        {
            return;
        }
        _childNodes ??= new ReadOnlyNodeList();
        var readOnlyTextNode = new ReadOnlyTextNode(Owner, text) { Parent = this };
        _childNodes.Insert(idx, readOnlyTextNode);
    }

    public void AddComment(ref StructHtmlToken token)
    {
        _childNodes ??= new ReadOnlyNodeList();
        var readOnlyTextNode = new ReadOnlyComment(Owner, token.Data) { Parent = this };
        _childNodes.Add(readOnlyTextNode);
    }

    public virtual void Print(TextWriter writer)
    {
        if (_childNodes == null)
        {
            return;
        }
        
        foreach (var node in _childNodes)
        {
            ((ReadOnlyNode)node).Print(writer);
        }
    }
}