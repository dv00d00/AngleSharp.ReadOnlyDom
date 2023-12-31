﻿using AngleSharp.Common;
using AngleSharp.Dom;

namespace AngleSharp.ReadOnlyDom.ReadOnly.Html.Model;

internal class ReadOnlyProcessingInstruction : ReadOnlyCharacterData, IReadOnlyProcessingInstructionNode
{
    private ReadOnlyProcessingInstruction(ReadOnlyDocument? owner, StringOrMemory name)
        : base(owner, name, NodeType.ProcessingInstruction)
    {
    }

    public static ReadOnlyProcessingInstruction Create(ReadOnlyDocument? owner, StringOrMemory tokenData)
    {
        return new ReadOnlyProcessingInstruction(owner, tokenData);
    }

    public override void Print(TextWriter writer)
    {
        writer.Write("<?");
        writer.Write(NodeName.Memory.Span);
        writer.Write(" ");
        writer.Write(Content.Memory.Span);
        writer.Write("?>");
    }
}