using AngleSharp.Common;

namespace AngleSharp.ReadOnlyDom.ReadOnly.Html;

public interface IReadOnlyCommentNode
{
    StringOrMemory Content { get; }
}