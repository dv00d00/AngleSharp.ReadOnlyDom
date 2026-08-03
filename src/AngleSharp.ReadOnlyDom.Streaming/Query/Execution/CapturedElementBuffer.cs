using System.Buffers;
using System.Text;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;

namespace AngleSharp.ReadOnlyDom.Streaming.Query.Execution;

internal sealed class CapturedElementBuffer : IDisposable
{
    private byte[] _utf8 = ArrayPool<byte>.Shared.Rent(256);
    private int[] _attributeStarts = ArrayPool<int>.Shared.Rent(1);
    private int[] _attributeLengths = ArrayPool<int>.Shared.Rent(1);
    private CompletedTextMode _textMode;
    private int _length;
    private int _textStart;
    private string? _decodedText;
    private bool _pendingSpace;
    private bool _disposed;

    internal ReadOnlySpan<byte> TextUtf8 => _utf8.AsSpan(_textStart, _length - _textStart);

    internal int BufferedByteCount => _length;

    internal void Reset(CompletedTextMode textMode, int attributeCount)
    {
        _textMode = textMode;
        _length = 0;
        _textStart = 0;
        _decodedText = null;
        _pendingSpace = false;
        EnsureAttributeCapacity(attributeCount);
        _attributeLengths.AsSpan(0, attributeCount).Fill(-1);
    }

    internal void SetAttribute(int index, ReadOnlySpan<byte> value)
    {
        EnsureUtf8Capacity(value.Length);
        _attributeStarts[index] = _length;
        _attributeLengths[index] = value.Length;
        value.CopyTo(_utf8.AsSpan(_length));
        _length += value.Length;
    }

    internal void BeginText() => _textStart = _length;

    internal bool TryGetAttribute(int index, out ReadOnlySpan<byte> value)
    {
        var length = _attributeLengths[index];
        if (length < 0)
        {
            value = default;
            return false;
        }
        value = _utf8.AsSpan(_attributeStarts[index], length);
        return true;
    }

    internal void Append(ReadOnlySpan<byte> utf8)
    {
        if (_textMode == CompletedTextMode.None)
            return;
        if (_textMode == CompletedTextMode.Raw)
        {
            AppendBytes(utf8);
            return;
        }
        var offset = 0;
        while (offset < utf8.Length)
        {
            var remaining = utf8[offset..];
            var runLength = TrustedUtf8.IndexOfWhiteSpace(remaining, out var whitespaceLength);
            if (runLength < 0)
                runLength = remaining.Length;
            if (runLength != 0)
            {
                if (_pendingSpace)
                {
                    AppendByte((byte)' ');
                    _pendingSpace = false;
                }
                AppendBytes(remaining[..runLength]);
                offset += runLength;
                continue;
            }

            offset += whitespaceLength;
            _pendingSpace = _length != _textStart;
        }
    }

    internal string GetText() => _decodedText ??= Encoding.UTF8.GetString(TextUtf8);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ArrayPool<byte>.Shared.Return(_utf8);
        ArrayPool<int>.Shared.Return(_attributeStarts, clearArray: true);
        ArrayPool<int>.Shared.Return(_attributeLengths, clearArray: true);
        _utf8 = [];
        _attributeStarts = [];
        _attributeLengths = [];
    }

    private void AppendByte(byte value)
    {
        EnsureUtf8Capacity(1);
        _utf8[_length++] = value;
    }

    private void AppendBytes(ReadOnlySpan<byte> value)
    {
        EnsureUtf8Capacity(value.Length);
        value.CopyTo(_utf8.AsSpan(_length));
        _length += value.Length;
    }

    private void EnsureUtf8Capacity(int additional)
    {
        if (_length + additional <= _utf8.Length)
            return;
        var replacement = ArrayPool<byte>.Shared.Rent(Math.Max(_utf8.Length * 2, _length + additional));
        _utf8.AsSpan(0, _length).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(_utf8);
        _utf8 = replacement;
    }

    private void EnsureAttributeCapacity(int count)
    {
        if (count <= _attributeStarts.Length)
            return;
        ArrayPool<int>.Shared.Return(_attributeStarts, clearArray: true);
        ArrayPool<int>.Shared.Return(_attributeLengths, clearArray: true);
        _attributeStarts = ArrayPool<int>.Shared.Rent(count);
        _attributeLengths = ArrayPool<int>.Shared.Rent(count);
    }
}
