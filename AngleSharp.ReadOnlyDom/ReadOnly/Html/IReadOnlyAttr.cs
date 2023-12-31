using AngleSharp.Common;

namespace AngleSharp.ReadOnlyDom.ReadOnly.Html;

public interface IReadOnlyAttr
{
    public StringOrMemory Name { get; }
    public StringOrMemory Value { get; }
}