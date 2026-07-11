using AngleSharp.Common;
using AngleSharp.Dom;

namespace AngleSharp.ReadOnlyDom.Html.Model;

internal class ReadOnlyDocumentType(ReadOnlyDocument document, StringOrMemory nameString)
    : ReadOnlyNode(document, nameString, NodeType.DocumentType)
{
    public StringOrMemory SystemIdentifier { get; set; }
    public StringOrMemory PublicIdentifier { get; set; }

    public override void Print(TextWriter writer)
    {
        writer.Write("<!DOCTYPE html ");
        writer.WriteSOM(PublicIdentifier);
        writer.Write(" ");
        writer.WriteSOM(SystemIdentifier);
        writer.WriteLine(">");
        base.Print(writer);
    }
}
