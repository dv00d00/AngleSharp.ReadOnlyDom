using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;
using AngleSharp.Html.Parser.Tokens.Struct;

namespace AngleSharp.ReadOnlyDom.Html.Model;

internal abstract class ReadOnlyNode
    : IConstructableNode,
        IReadOnlyNode,
        IConstructableNodeList,
        IReadOnlyNodeList,
        IPrintable
{
    private static readonly ReadOnlyNodeList EmptyChildNodes = [];
    private static ReadOnlySpan<char> WhiteSpace => " \t\r\n".AsSpan();

    protected readonly NodeFlags _flags;
    protected IConstructableNodeList? _childNodes;
    protected IConstructableNode? _parent;
    protected StringOrMemory _nodeName;

    public NodeFlags Flags => _flags;
    protected IConstructableNodeList _ChildNodes => _childNodes ?? EmptyChildNodes;
    IReadOnlyNode? IReadOnlyNode.Parent => _parent as IReadOnlyNode;
    IReadOnlyNodeList IReadOnlyNode.ChildNodes => (IReadOnlyNodeList)_ChildNodes;

    public StringOrMemory NodeName
    {
        get => _nodeName;
        internal set => _nodeName = value;
    }

    public ReadOnlyDocument? Owner => null;

    public ReadOnlyNode(
        ReadOnlyDocument? owner,
        StringOrMemory name,
        NodeType nodeType = NodeType.Element,
        NodeFlags flags = NodeFlags.None
    )
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
        if (_childNodes is ReadOnlyNode singleton)
        {
            if (ReferenceEquals(singleton, childNode))
            {
                _childNodes = null;
            }
        }
        else
        {
            ((ReadOnlyNodeList?)_childNodes)?.Remove(childNode);
        }
    }

    public void RemoveNode(int idx, IConstructableNode childNode)
    {
        childNode.Parent = null;
        if (_childNodes is ReadOnlyNode)
        {
            if (idx != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(idx));
            }

            _childNodes = null;
        }
        else
        {
            ((ReadOnlyNodeList?)_childNodes)?.RemoveAt(idx);
        }
    }

    public void InsertNode(int idx, IConstructableNode childNode)
    {
        childNode.Parent = this;
        if (_childNodes is null)
        {
            if (idx != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(idx));
            }

            _childNodes = (ReadOnlyNode)childNode;
            return;
        }

        ExpandChildNodes().Insert(idx, childNode);
    }

    public void AddNode(IConstructableNode childNode)
    {
        childNode.Parent = this;
        if (_childNodes is null)
        {
            _childNodes = (ReadOnlyNode)childNode;
            return;
        }

        ExpandChildNodes().Add(childNode);
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

        var readOnlyTextNode = new ReadOnlyTextNode(Owner, text) { Parent = this };
        InsertNode(idx, readOnlyTextNode);
    }

    public void AddComment(ref StructHtmlToken token)
    {
        var readOnlyTextNode = new ReadOnlyComment(Owner, token.Data) { Parent = this };
        AddNode(readOnlyTextNode);
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

    int IConstructableNodeList.Length => 1;
    int IReadOnlyNodeList.Length => 1;

    IConstructableNode IConstructableNodeList.this[int index] =>
        index == 0 ? this : throw new ArgumentOutOfRangeException(nameof(index));

    IReadOnlyNode IReadOnlyNodeList.this[int index] =>
        index == 0 ? this : throw new ArgumentOutOfRangeException(nameof(index));

    IEnumerator<IConstructableNode> IEnumerable<IConstructableNode>.GetEnumerator()
    {
        yield return this;
    }

    IEnumerator<IReadOnlyNode> IEnumerable<IReadOnlyNode>.GetEnumerator()
    {
        yield return this;
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        ((IEnumerable<IConstructableNode>)this).GetEnumerator();

    void IConstructableNodeList.Clear() => Parent?.RemoveChild(this);

    private ReadOnlyNodeList ExpandChildNodes()
    {
        if (_childNodes is ReadOnlyNode singleton)
        {
            var nodes = new ReadOnlyNodeList();
            nodes.Add(singleton);
            _childNodes = nodes;
            return nodes;
        }

        return (ReadOnlyNodeList)_childNodes!;
    }
}
