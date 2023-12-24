using AngleSharp.Common;
using AngleSharp.Dom;

namespace AngleSharp.ReadOnlyDom.ReadOnly.Html;

public interface IReadOnlyElement : IReadOnlyNode
{
    StringOrMemory NamespaceUri { get; }
    StringOrMemory LocalName { get; }
    IReadOnlyNamedNodeMap Attributes { get; }
    ISourceReference? SourceReference { get; }
}