using AngleSharp.Common;

namespace AngleSharp.ReadOnlyDom.ReadOnly.Html;

public interface IReadOnlyTextNode
{
    StringOrMemory Content { get; }
}