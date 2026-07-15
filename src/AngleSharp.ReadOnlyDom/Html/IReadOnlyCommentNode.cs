using AngleSharp.Common;

namespace AngleSharp.ReadOnlyDom.Html;

public interface IReadOnlyCommentNode
{
    StringOrMemory Content { get; }
}
