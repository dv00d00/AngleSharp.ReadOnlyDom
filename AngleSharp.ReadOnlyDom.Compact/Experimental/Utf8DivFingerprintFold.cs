#if NET10_0
using System.Buffers;
using System.Text;

namespace AngleSharp.ReadOnlyDom.Compact.Experimental;

/// <summary>
/// Query-directed native UTF-8 fold used to validate the streaming extraction architecture.
/// This is not an HTML tree builder: it maintains a lexical open-element stack and handles void elements,
/// but does not implement adoption agency, foster parenting, or implied end tags.
/// </summary>
public sealed class Utf8DivFingerprintFold : IUtf8HtmlTokenSink, IDisposable
{
    public const ulong OffsetBasis = 14695981039346656037UL;
    public const ulong Prime = 1099511628211UL;

    private Frame[] _frames = ArrayPool<Frame>.Shared.Rent(32);
    private DivResult[] _results = ArrayPool<DivResult>.Shared.Rent(64);
    private int _frameCount;
    private int _resultCount;
    private ulong _pendingTagHash;
    private int _pendingTagLength;
    private bool _pendingIsDiv;
    private bool _pendingIsTemplate;
    private bool _pendingIsVoid;
    private ulong _pendingIdHash;
    private ulong _pendingClassHash;
    private bool _pendingHasId;
    private bool _pendingHasClass;
    private int _templateDepth;
    private bool _disposed;

    public int MatchCount => _resultCount;

    public (ulong IdHash, ulong ClassHash, ulong TextHash) GetMatch(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _resultCount);
        ref var result = ref _results[index];
        return (result.IdHash, result.ClassHash, result.TextHash);
    }

    public ulong Fingerprint
    {
        get
        {
            var hash = OffsetBasis;
            for (var index = 0; index < _resultCount; index++)
            {
                AppendUInt64(ref hash, _results[index].IdHash);
                AppendUInt64(ref hash, _results[index].ClassHash);
                AppendUInt64(ref hash, _results[index].TextHash);
            }
            AppendUInt64(ref hash, (ulong)_resultCount);
            return hash;
        }
    }

    public void StartTag(ReadOnlySpan<byte> name)
    {
        _pendingTagHash = HashAscii(name);
        _pendingTagLength = name.Length;
        _pendingIsDiv = name.SequenceEqual("div"u8);
        _pendingIsTemplate = name.SequenceEqual("template"u8);
        _pendingIsVoid = IsVoid(name);
        _pendingIdHash = OffsetBasis;
        _pendingClassHash = OffsetBasis;
        _pendingHasId = false;
        _pendingHasClass = false;
    }

    public void Attribute(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        if (!_pendingIsDiv)
            return;

        if (!_pendingHasId && name.SequenceEqual("id"u8))
        {
            _pendingIdHash = HashUtf8(value);
            _pendingHasId = true;
        }
        else if (!_pendingHasClass && name.SequenceEqual("class"u8))
        {
            _pendingClassHash = HashUtf8(value);
            _pendingHasClass = true;
        }
    }

    public void StartTagEnd(bool selfClosing)
    {
        var resultIndex = -1;
        if (_pendingIsDiv && _templateDepth == 0)
        {
            EnsureResultCapacity();
            resultIndex = _resultCount++;
            _results[resultIndex] = new DivResult(
                _pendingHasId ? _pendingIdHash : OffsetBasis,
                _pendingHasClass ? _pendingClassHash : OffsetBasis,
                OffsetBasis
            );
        }

        if (!selfClosing && !_pendingIsVoid)
        {
            EnsureFrameCapacity();
            _frames[_frameCount++] = new Frame(
                _pendingTagHash,
                _pendingTagLength,
                resultIndex,
                _pendingIsTemplate
            );
            if (_pendingIsTemplate)
                _templateDepth++;
        }
    }

    public void Text(ReadOnlySpan<byte> utf8)
    {
        if (_frameCount == 0 || _templateDepth != 0 || utf8.IsEmpty)
            return;

        var remaining = utf8;
        Span<char> chars = stackalloc char[2];
        while (!remaining.IsEmpty)
        {
            uint first;
            uint second = uint.MaxValue;
            int consumed;
            if (remaining[0] < 0x80)
            {
                first = remaining[0];
                consumed = 1;
            }
            else if (Rune.DecodeFromUtf8(remaining, out var rune, out consumed) == OperationStatus.Done)
            {
                var written = rune.EncodeToUtf16(chars);
                first = chars[0];
                if (written == 2)
                    second = chars[1];
            }
            else
            {
                first = '\uFFFD';
                consumed = 1;
            }

            for (var index = 0; index < _frameCount; index++)
            {
                var resultIndex = _frames[index].ResultIndex;
                if (resultIndex < 0)
                    continue;
                AppendChar(ref _results[resultIndex].TextHash, first);
                if (second != uint.MaxValue)
                    AppendChar(ref _results[resultIndex].TextHash, second);
            }
            remaining = remaining[consumed..];
        }
    }

    public void EndTag(ReadOnlySpan<byte> name)
    {
        var hash = HashAscii(name);
        for (var index = _frameCount - 1; index >= 0; index--)
        {
            if (_frames[index].TagHash == hash && _frames[index].TagLength == name.Length)
            {
                for (var popped = index; popped < _frameCount; popped++)
                {
                    if (_frames[popped].IsTemplate)
                        _templateDepth--;
                }
                _frameCount = index;
                return;
            }
        }
    }

    public void EndOfFile()
    {
        _frameCount = 0;
        _templateDepth = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ArrayPool<Frame>.Shared.Return(_frames);
        ArrayPool<DivResult>.Shared.Return(_results);
        _frames = [];
        _results = [];
        _frameCount = 0;
        _resultCount = 0;
        _templateDepth = 0;
    }

    public static ulong HashChars(ReadOnlySpan<char> value)
    {
        var hash = OffsetBasis;
        foreach (var character in value)
            AppendChar(ref hash, character);
        return hash;
    }

    public static void AppendUInt64(ref ulong hash, ulong value)
    {
        for (var shift = 0; shift < 64; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= Prime;
        }
    }

    private static ulong HashUtf8(ReadOnlySpan<byte> utf8)
    {
        var hash = OffsetBasis;
        var remaining = utf8;
        Span<char> chars = stackalloc char[2];
        while (!remaining.IsEmpty)
        {
            if (remaining[0] < 0x80)
            {
                AppendChar(ref hash, remaining[0]);
                remaining = remaining[1..];
                continue;
            }

            if (Rune.DecodeFromUtf8(remaining, out var rune, out var consumed) != OperationStatus.Done)
            {
                AppendChar(ref hash, '\uFFFD');
                remaining = remaining[1..];
                continue;
            }

            var written = rune.EncodeToUtf16(chars);
            for (var index = 0; index < written; index++)
                AppendChar(ref hash, chars[index]);
            remaining = remaining[consumed..];
        }
        return hash;
    }

    private static ulong HashAscii(ReadOnlySpan<byte> value)
    {
        var hash = OffsetBasis;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= Prime;
        }
        return hash;
    }

    private static void AppendChar(ref ulong hash, uint character)
    {
        hash ^= (ushort)character;
        hash *= Prime;
    }

    private static bool IsVoid(ReadOnlySpan<byte> name) =>
        name.SequenceEqual("area"u8)
        || name.SequenceEqual("base"u8)
        || name.SequenceEqual("br"u8)
        || name.SequenceEqual("col"u8)
        || name.SequenceEqual("embed"u8)
        || name.SequenceEqual("hr"u8)
        || name.SequenceEqual("img"u8)
        || name.SequenceEqual("input"u8)
        || name.SequenceEqual("link"u8)
        || name.SequenceEqual("meta"u8)
        || name.SequenceEqual("param"u8)
        || name.SequenceEqual("source"u8)
        || name.SequenceEqual("track"u8)
        || name.SequenceEqual("wbr"u8);

    private void EnsureFrameCapacity()
    {
        if (_frameCount < _frames.Length)
            return;
        var replacement = ArrayPool<Frame>.Shared.Rent(_frames.Length * 2);
        _frames.AsSpan(0, _frameCount).CopyTo(replacement);
        ArrayPool<Frame>.Shared.Return(_frames);
        _frames = replacement;
    }

    private void EnsureResultCapacity()
    {
        if (_resultCount < _results.Length)
            return;
        var replacement = ArrayPool<DivResult>.Shared.Rent(_results.Length * 2);
        _results.AsSpan(0, _resultCount).CopyTo(replacement);
        ArrayPool<DivResult>.Shared.Return(_results);
        _results = replacement;
    }

    private readonly record struct Frame(
        ulong TagHash,
        int TagLength,
        int ResultIndex,
        bool IsTemplate
    );

    private struct DivResult(ulong idHash, ulong classHash, ulong textHash)
    {
        public ulong IdHash = idHash;
        public ulong ClassHash = classHash;
        public ulong TextHash = textHash;
    }
}
#endif
