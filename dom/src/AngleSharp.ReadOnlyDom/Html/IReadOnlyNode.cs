using AngleSharp.Common;
using AngleSharp.Dom;

namespace AngleSharp.ReadOnlyDom.Html;

public interface IReadOnlyNode
{
    StringOrMemory NodeName { get; }
    NodeFlags Flags { get; }
    IReadOnlyNode? Parent { get; }
    IReadOnlyNodeList ChildNodes { get; }
    void Print(TextWriter writer);
}
