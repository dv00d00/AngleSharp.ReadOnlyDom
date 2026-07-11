using AngleSharp.Common;

namespace AngleSharp.ReadOnlyDom.Html;

public interface IReadOnlyTextNode
{
    StringOrMemory Content { get; }
}