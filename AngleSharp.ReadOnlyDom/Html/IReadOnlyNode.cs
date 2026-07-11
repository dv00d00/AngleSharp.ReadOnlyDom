using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.ReadOnlyDom.Html.Model;

namespace AngleSharp.ReadOnlyDom.Html;

public interface IReadOnlyNode : IPrintable
{
    StringOrMemory NodeName { get; }
    NodeFlags Flags { get; }
    IReadOnlyNode? Parent { get; }
    IReadOnlyNodeList ChildNodes { get; }
}