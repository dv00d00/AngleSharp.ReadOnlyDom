using AngleSharp.Common;

namespace AngleSharp.ReadOnlyDom.ReadOnly.Html;

public interface IReadOnlyNamedNodeMap : IEnumerable<IReadOnlyAttr>
{
    IReadOnlyAttr? this[StringOrMemory name] { get; }
    int Length { get; }
}