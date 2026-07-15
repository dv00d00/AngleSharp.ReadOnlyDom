using AngleSharp.Common;

namespace AngleSharp.ReadOnlyDom.Html;

public interface IReadOnlyAttr
{
    public StringOrMemory Name { get; }
    public StringOrMemory Value { get; }
}
