namespace AngleSharp.ReadOnly.Html;

using Common;
using Dom;

public interface IReadOnlyNode
{
    StringOrMemory NodeName { get; }
    NodeFlags Flags { get; }
    IReadOnlyNode? Parent { get; }
    IReadOnlyNodeList ChildNodes { get; }
}