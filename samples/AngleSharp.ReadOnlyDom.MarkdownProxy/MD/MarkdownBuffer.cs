using System.Buffers;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming;

internal sealed class MarkdownBuffer : IUtf8PublishSource
{
    private readonly PublishableUtf8Buffer _output = new(4 * 1024);
    private readonly ArrayBufferWriter<byte> _row = new(256);
    private readonly ArrayBufferWriter<byte> _linkTarget = new(128);
    private readonly ArrayBufferWriter<byte> _documentTitle = new(128);
    private readonly ArrayBufferWriter<byte> _inlinePrefix = new(16);

    private int _tableDepth;
    private int _rowCells;
    private int _inlineBlockDepth;
    private int _preferredArticleDepth;
    private int _layoutTableDepth;
    private bool _firstTableRow;
    private bool _inlineLink;
    private bool _inlineLinkHasContent;
    private bool _inlineBlockHasContent;
    private bool _spaceBeforeInlineLink;
    private bool _pendingInlineSpace;
    private bool _preferredArticleFound;
    private bool _preferredArticleComplete;

    internal ReadOnlyMemory<byte> WrittenMemory => _output.WrittenUtf8;

    public ReadOnlyMemory<byte> PublishableUtf8 => _output.PublishableUtf8;

    public void AdvancePublished(int bytes) => _output.AdvancePublished(bytes);

    private bool AcceptsContent => !_preferredArticleFound || !_preferredArticleComplete && _preferredArticleDepth != 0;

    internal void DocumentTitle(ReadOnlySpan<byte> title)
    {
        _documentTitle.Clear();
        Write(_documentTitle, title);
        Block("# "u8, title);
    }

    internal void StartPreferredArticle()
    {
        if (_preferredArticleFound)
        {
            if (!_preferredArticleComplete && _preferredArticleDepth != 0)
                _preferredArticleDepth++;
            return;
        }

        _preferredArticleFound = true;
        _preferredArticleDepth = 1;
        _output.Clear();
        _tableDepth = 0;
        _inlineBlockDepth = 0;
        _inlineLink = false;
        _pendingInlineSpace = false;
        if (!_documentTitle.WrittenSpan.IsEmpty)
            Block("# "u8, _documentTitle.WrittenSpan);
    }

    internal void EndPreferredArticle()
    {
        if (!_preferredArticleFound || _preferredArticleComplete || _preferredArticleDepth == 0)
            return;
        _preferredArticleDepth--;
        if (_preferredArticleDepth == 0)
        {
            _output.MarkPublishable();
            _preferredArticleComplete = true;
        }
    }

    internal void CompleteDocument()
    {
        if (!_preferredArticleFound)
            _output.MarkPublishable();
    }

    internal void Block(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> text)
    {
        if (!AcceptsContent || _tableDepth != 0 || prefix.IsEmpty && text.IsEmpty)
            return;
        Write(prefix);
        Write(text);
        Write("\n\n"u8);
        CommitIfSafe();
    }

    internal void StartInlineBlock(ReadOnlySpan<byte> prefix)
    {
        if (!AcceptsContent || _tableDepth != 0)
            return;
        if (_inlineBlockDepth == 0)
        {
            _inlinePrefix.Clear();
            Write(_inlinePrefix, prefix);
            _inlineBlockHasContent = false;
            _pendingInlineSpace = false;
        }
        _inlineBlockDepth++;
    }

    internal void AppendInlineText(ReadOnlySpan<byte> utf8)
    {
        if (_inlineBlockDepth == 0 || _tableDepth != 0)
            return;
        while (!utf8.IsEmpty)
        {
            var status = Rune.DecodeFromUtf8(utf8, out var rune, out var consumed);
            if (status != OperationStatus.Done)
                throw new InvalidOperationException("The tokenizer emitted incomplete UTF-8 text.");
            var scalar = utf8[..consumed];
            utf8 = utf8[consumed..];
            if (Rune.IsWhiteSpace(rune))
            {
                _pendingInlineSpace = true;
                continue;
            }
            EnsureInlineBlockStarted();
            EnsureInlineLinkStarted();
            FlushInlineSpace();
            Write(scalar);
        }
    }

    internal void EndInlineBlock()
    {
        if (_inlineBlockDepth == 0 || _tableDepth != 0)
            return;
        _inlineBlockDepth--;
        _pendingInlineSpace = false;
        if (_inlineBlockDepth == 0 && _inlineBlockHasContent)
        {
            Write("\n\n"u8);
            CommitIfSafe();
        }
    }

    internal void StartInlineLink(ReadOnlySpan<byte> href)
    {
        if (_inlineBlockDepth == 0 || _tableDepth != 0 || _inlineLink)
            return;
        _linkTarget.Clear();
        Write(_linkTarget, href);
        _spaceBeforeInlineLink = _pendingInlineSpace;
        _pendingInlineSpace = false;
        _inlineLink = true;
        _inlineLinkHasContent = false;
    }

    internal void EndInlineLink()
    {
        if (!_inlineLink)
            return;
        if (!_inlineLinkHasContent)
        {
            _pendingInlineSpace |= _spaceBeforeInlineLink;
            _inlineLink = false;
            return;
        }
        _pendingInlineSpace = false;
        Write("]("u8);
        Write(_linkTarget.WrittenSpan);
        Write(")"u8);
        _inlineLink = false;
    }

    internal void FencedCode(ReadOnlySpan<byte> text)
    {
        if (!AcceptsContent || _tableDepth != 0 || text.IsEmpty)
            return;
        Write("```\n"u8);
        Write(text);
        if (text[^1] != (byte)'\n')
            Write("\n"u8);
        Write("```\n\n"u8);
        CommitIfSafe();
    }

    internal void Image(ReadOnlySpan<byte> alt, ReadOnlySpan<byte> source)
    {
        if (!AcceptsContent || _tableDepth != 0)
            return;
        Write("!["u8);
        Write(alt);
        Write("]("u8);
        Write(source);
        Write(")\n\n"u8);
        CommitIfSafe();
    }

    internal void StartLayoutTable()
    {
        _layoutTableDepth++;
        _tableDepth = 0;
        _row.Clear();
        _rowCells = 0;
    }

    internal void EndLayoutTable()
    {
        if (_layoutTableDepth != 0)
            _layoutTableDepth--;
    }

    internal void StartTable()
    {
        if (!AcceptsContent || _layoutTableDepth != 0)
            return;
        _tableDepth++;
        if (_tableDepth == 1)
            _firstTableRow = true;
    }

    internal void EndTable()
    {
        if (_layoutTableDepth != 0 || _tableDepth == 0)
            return;
        _tableDepth--;
        if (_tableDepth == 0)
        {
            Write("\n"u8);
            CommitIfSafe();
        }
    }

    internal void StartRow()
    {
        if (_tableDepth != 1)
            return;
        _row.Clear();
        _rowCells = 0;
    }

    internal void Cell(ReadOnlySpan<byte> text)
    {
        if (_tableDepth != 1)
            return;
        Write(_row, "| "u8);
        WriteEscapedCell(text);
        Write(_row, " "u8);
        _rowCells++;
    }

    internal void EndRow()
    {
        if (_tableDepth != 1 || _rowCells == 0)
            return;
        Write(_row, "|\n"u8);
        Write(_row.WrittenSpan);
        if (_firstTableRow)
        {
            for (var cell = 0; cell < _rowCells; cell++)
                Write("| --- "u8);
            Write("|\n"u8);
            _firstTableRow = false;
        }
        CommitIfSafe();
    }

    private void WriteEscapedCell(ReadOnlySpan<byte> value)
    {
        foreach (var character in value)
        {
            if (character == (byte)'|')
                Write(_row, "\\"u8);
            Write(_row, [character]);
        }
    }

    private void Write(ReadOnlySpan<byte> value)
    {
        Write(_output, value);
    }

    private void FlushInlineSpace()
    {
        if (!_pendingInlineSpace)
            return;
        Write(" "u8);
        _pendingInlineSpace = false;
    }

    private void EnsureInlineBlockStarted()
    {
        if (_inlineBlockHasContent)
            return;
        Write(_inlinePrefix.WrittenSpan);
        _inlineBlockHasContent = true;
    }

    private void EnsureInlineLinkStarted()
    {
        if (!_inlineLink || _inlineLinkHasContent)
            return;
        _pendingInlineSpace = _spaceBeforeInlineLink;
        FlushInlineSpace();
        Write("["u8);
        _inlineLinkHasContent = true;
    }

    private void CommitIfSafe()
    {
        if (_preferredArticleFound && !_preferredArticleComplete && _preferredArticleDepth != 0)
            _output.MarkPublishable();
    }

    private static void Write(IBufferWriter<byte> output, ReadOnlySpan<byte> value)
    {
        var destination = output.GetSpan(value.Length);
        value.CopyTo(destination);
        output.Advance(value.Length);
    }
}
