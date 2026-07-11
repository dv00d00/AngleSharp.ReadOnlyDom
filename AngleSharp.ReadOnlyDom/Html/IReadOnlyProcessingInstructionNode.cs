using AngleSharp.Common;

namespace AngleSharp.ReadOnlyDom.Html;

public interface IReadOnlyProcessingInstructionNode
{
    StringOrMemory Content { get; }
}