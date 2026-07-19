using System.Buffers;
namespace AngleSharp.ReadOnlyDom.Streaming;

/// <summary>
/// Writes UTF-8 text while collapsing source whitespace and delaying semantic separators until
/// more content arrives, avoiding leading and trailing separators.
/// </summary>
internal sealed class NormalizedUtf8Writer
{
    private static readonly byte[] DefaultLineSeparator = "\n"u8.ToArray();
    private static readonly byte[] DefaultParagraphSeparator = "\n\n"u8.ToArray();
    private static readonly byte[] DefaultCellSeparator = "\t"u8.ToArray();

    private readonly IBufferWriter<byte> _output;
    private readonly ReadOnlyMemory<byte> _lineSeparator;
    private readonly ReadOnlyMemory<byte> _paragraphSeparator;
    private readonly ReadOnlyMemory<byte> _cellSeparator;
    private PendingSeparator _pending;
    private bool _hasContent;

    internal NormalizedUtf8Writer(IBufferWriter<byte> output)
        : this(output, DefaultLineSeparator, DefaultParagraphSeparator, DefaultCellSeparator) { }

    internal NormalizedUtf8Writer(
        IBufferWriter<byte> output,
        ReadOnlyMemory<byte> lineSeparator,
        ReadOnlyMemory<byte> paragraphSeparator,
        ReadOnlyMemory<byte> cellSeparator
    )
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _lineSeparator = lineSeparator;
        _paragraphSeparator = paragraphSeparator;
        _cellSeparator = cellSeparator;
    }

    /// <summary>Appends UTF-8 text, collapsing HTML ASCII whitespace and NBSP runs.</summary>
    internal void Append(ReadOnlySpan<byte> utf8)
    {
        var offset = 0;
        while (offset < utf8.Length)
        {
            var remaining = utf8[offset..];
            var runLength = TrustedUtf8.IndexOfWhiteSpace(remaining, out var whitespaceLength);
            if (runLength < 0)
                runLength = remaining.Length;
            if (runLength != 0)
            {
                FlushPending();
                Write(remaining[..runLength]);
                _hasContent = true;
                offset += runLength;
                continue;
            }

            offset += whitespaceLength;
            Request(PendingSeparator.Space);
        }
    }

    internal void Space() => Request(PendingSeparator.Space);

    internal void CellBreak() => Request(PendingSeparator.Cell);

    internal void LineBreak() => Request(PendingSeparator.Line);

    internal void ParagraphBreak() => Request(PendingSeparator.Paragraph);

    /// <summary>Resets normalization state without modifying the destination.</summary>
    internal void Reset()
    {
        _pending = PendingSeparator.None;
        _hasContent = false;
    }

    private void Request(PendingSeparator separator)
    {
        if (_hasContent && separator > _pending)
            _pending = separator;
    }

    private void FlushPending()
    {
        if (!_hasContent)
        {
            _pending = PendingSeparator.None;
            return;
        }

        var separator = _pending switch
        {
            PendingSeparator.Space => " "u8,
            PendingSeparator.Cell => _cellSeparator.Span,
            PendingSeparator.Line => _lineSeparator.Span,
            PendingSeparator.Paragraph => _paragraphSeparator.Span,
            _ => default,
        };
        Write(separator);
        _pending = PendingSeparator.None;
    }

    private void Write(ReadOnlySpan<byte> value)
    {
        value.CopyTo(_output.GetSpan(value.Length));
        _output.Advance(value.Length);
    }

    private enum PendingSeparator : byte
    {
        None,
        Space,
        Cell,
        Line,
        Paragraph,
    }
}
