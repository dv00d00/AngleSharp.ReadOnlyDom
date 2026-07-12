using AngleSharp.Common;
using AngleSharp.Dom;

namespace AngleSharp.ReadOnlyDom.Html;

public interface IReadOnlyElement : IReadOnlyNode
{
    StringOrMemory NamespaceUri { get; }
    StringOrMemory LocalName { get; }
    StringOrMemory Prefix { get; }
    IReadOnlyNamedNodeMap Attributes { get; }
    ISourceReference? SourceReference { get; }
}

public interface IReadOnlyTemplateElement : IReadOnlyElement
{
    IReadOnlyNodeList Content { get; }
}
