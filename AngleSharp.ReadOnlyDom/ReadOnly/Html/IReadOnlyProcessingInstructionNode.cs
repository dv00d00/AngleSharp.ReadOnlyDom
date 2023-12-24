using AngleSharp.Common;

namespace AngleSharp.ReadOnlyDom.ReadOnly.Html;

public interface IReadOnlyProcessingInstructionNode
{
    StringOrMemory Content { get; }
}