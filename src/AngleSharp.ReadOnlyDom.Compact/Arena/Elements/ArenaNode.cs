using System.Collections;
using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;
using AngleSharp.Html.Parser.Tokens.Struct;

namespace AngleSharp.ReadOnlyDom.Compact.Arena;

internal class ArenaNode : IConstructableNode, IConstructableNodeList
{
    protected internal ArenaNode(Arena arena, int handle)
    {
        Arena = arena;
        NodeHandle = handle;
    }

    internal Arena Arena { get; }
    internal int NodeHandle { get; }
    public StringOrMemory NodeName => Arena.Name(NodeHandle);
    public NodeFlags Flags => Arena.Flags(NodeHandle);
    public IConstructableNode? Parent
    {
        get
        {
            var parent = Arena.Parent(NodeHandle);
            return parent < 0 ? null : Arena.Node(parent);
        }
        set
        {
            if (value is ArenaNode node)
                Arena.AddChild(node.NodeHandle, NodeHandle);
            else
                Arena.RemoveFromParent(NodeHandle);
        }
    }
    public IConstructableNodeList ChildNodes => this;
    public int Length => Arena.ChildCount(NodeHandle);
    public IConstructableNode this[int index] => Arena.Node(Arena.ChildAt(NodeHandle, index));

    public void AddNode(IConstructableNode node) => Arena.AddChild(NodeHandle, ((ArenaNode)node).NodeHandle);

    public void InsertNode(int index, IConstructableNode node) =>
        Arena.AddChild(NodeHandle, ((ArenaNode)node).NodeHandle, index);

    public void AppendText(StringOrMemory text, bool emitWhiteSpaceOnly = false) =>
        Arena.AddText(NodeHandle, text, emitWhiteSpaceOnly);

    public void InsertText(int index, StringOrMemory text, bool emitWhiteSpaceOnly = false) =>
        Arena.AddText(NodeHandle, text, emitWhiteSpaceOnly, index);

    public void AddComment(ref StructHtmlToken token) => Arena.AddComment(NodeHandle, ref token);

    public void RemoveFromParent() => Arena.RemoveFromParent(NodeHandle);

    public void RemoveChild(IConstructableNode childNode)
    {
        var child = (ArenaNode)childNode;
        Arena.RemoveChild(NodeHandle, child.NodeHandle);
    }

    public void RemoveNode(int index, IConstructableNode childNode)
    {
        if (Arena.ChildAt(NodeHandle, index) != ((ArenaNode)childNode).NodeHandle)
            throw new ArgumentException(
                "The supplied node does not match the child at the requested index.",
                nameof(childNode)
            );
        Arena.RemoveChild(NodeHandle, ((ArenaNode)childNode).NodeHandle);
    }

    public void Clear() => Arena.ClearChildren(NodeHandle);

    public IEnumerator<IConstructableNode> GetEnumerator()
    {
        for (var index = 0; index < Arena.ChildCount(NodeHandle); index++)
            yield return Arena.Node(Arena.ChildAt(NodeHandle, index));
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
