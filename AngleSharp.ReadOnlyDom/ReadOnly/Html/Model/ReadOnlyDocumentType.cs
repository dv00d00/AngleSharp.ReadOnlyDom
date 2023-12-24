using AngleSharp.Common;
using AngleSharp.Dom;

namespace AngleSharp.ReadOnlyDom.ReadOnly.Html.Model;

internal class ReadOnlyDocumentType : ReadOnlyNode
{
    public ReadOnlyDocumentType(ReadOnlyDocument document, StringOrMemory nameString) : base(document, nameString, NodeType.DocumentType)
    {
    }

    public StringOrMemory SystemIdentifier { get; set; }
    public StringOrMemory PublicIdentifier { get; set; }

    public override void Print(TextWriter writer)
    {
        writer.Write("<!DOCTYPE html ");
        writer.Write(PublicIdentifier.Memory.Span);
        writer.Write(" ");
        writer.Write(SystemIdentifier.Memory.Span);
        writer.WriteLine(">");
        base.Print(writer);
    }
}