using AngleSharp.Common;
using AngleSharp.Dom;

namespace AngleSharp.ReadOnlyDom.Html.Model;

internal class ReadOnlyProcessingInstruction : ReadOnlyCharacterData, IReadOnlyProcessingInstructionNode
{
    private readonly StringOrMemory _target;

    private ReadOnlyProcessingInstruction(ReadOnlyDocument? owner, StringOrMemory target, StringOrMemory content)
        : base(owner, content)
    {
        _target = target;
    }

    public override StringOrMemory NodeName => _target;
    public StringOrMemory Target => _target;

    public static ReadOnlyProcessingInstruction Create(ReadOnlyDocument? owner, StringOrMemory tokenData)
    {
        var data = tokenData.Memory;
        var separator = data.Span.IndexOf(' ');
        return separator <= 0
            ? new ReadOnlyProcessingInstruction(owner, tokenData, default)
            : new ReadOnlyProcessingInstruction(owner, data.Slice(0, separator), data.Slice(separator));
    }

    public override void Print(TextWriter writer)
    {
        writer.Write("<?");
        writer.WriteSOM(Target);
        writer.WriteSOM(Content);
        writer.Write("?>");
    }
}
