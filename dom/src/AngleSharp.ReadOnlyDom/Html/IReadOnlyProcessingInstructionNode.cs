using AngleSharp.Common;

namespace AngleSharp.ReadOnlyDom.Html;

public interface IReadOnlyProcessingInstructionNode
{
    StringOrMemory Target { get; }
    StringOrMemory Content { get; }
}
