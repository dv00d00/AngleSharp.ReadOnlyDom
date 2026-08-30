using System.Buffers;
using System.Text;

namespace AngleSharp.Streaming.ExtractionExamples;

internal sealed class NormalizedTextOutput
{
    private readonly StringBuilder _output = new();
    private Separator _pending;
    internal string Value => _output.ToString();

    internal void AppendUtf8(ReadOnlySpan<byte> utf8)
    {
        var length = Encoding.UTF8.GetCharCount(utf8);
        var rented = length > 256 ? ArrayPool<char>.Shared.Rent(length) : null;
        Span<char> characters = rented is null ? stackalloc char[length] : rented.AsSpan(0, length);
        try
        {
            var written = Encoding.UTF8.GetChars(utf8, characters);
            Append(characters[..written]);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    internal void Append(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character) || character == '\u00A0')
            {
                Schedule(Separator.Space);
                continue;
            }
            Flush();
            _output.Append(character);
        }
    }

    internal void Space() => Schedule(Separator.Space);

    internal void CellBreak() => Schedule(Separator.Cell);

    internal void LineBreak() => Schedule(Separator.Line);

    internal void ParagraphBreak() => Schedule(Separator.Paragraph);

    private void Schedule(Separator separator)
    {
        if (separator > _pending)
            _pending = separator;
    }

    private void Flush()
    {
        if (_output.Length == 0)
        {
            _pending = Separator.None;
            return;
        }
        _output.Append(
            _pending switch
            {
                Separator.Space => " ",
                Separator.Cell => "\t",
                Separator.Line => "\n",
                Separator.Paragraph => "\n\n",
                _ => String.Empty,
            }
        );
        _pending = Separator.None;
    }

    private enum Separator : byte
    {
        None,
        Space,
        Cell,
        Line,
        Paragraph,
    }
}
