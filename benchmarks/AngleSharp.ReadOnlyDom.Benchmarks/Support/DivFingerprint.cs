#if NET10_0
using System.Buffers;
using AngleSharp.ReadOnlyDom.Streaming.Internal;
using AngleSharp.ReadOnlyDom.Streaming.Public;

namespace AngleSharp.ReadOnlyDom.Benchmarks.Support;

internal static class DivFingerprint
{
    internal const ulong OffsetBasis = 14695981039346656037UL;
    internal const ulong Prime = 1099511628211UL;

    internal static ulong HashChars(ReadOnlySpan<char> value)
    {
        var hash = OffsetBasis;
        foreach (var character in value)
            AppendChar(ref hash, character);
        return hash;
    }

    internal static ulong HashUtf8(ReadOnlySpan<byte> utf8)
    {
        var hash = OffsetBasis;
        AppendUtf8(ref hash, utf8);
        return hash;
    }

    internal static void AppendUtf8(ref ulong hash, ReadOnlySpan<byte> utf8)
    {
        var offset = 0;
        while (offset < utf8.Length)
        {
            var firstByte = utf8[offset];
            if (firstByte < 0x80)
            {
                AppendChar(ref hash, firstByte);
                offset++;
                continue;
            }

            var length = TrustedUtf8.SequenceLength(firstByte);
            var scalar = TrustedUtf8.DecodeScalar(utf8[offset..], length);
            offset += length;
            if (scalar <= 0xFFFF)
            {
                AppendChar(ref hash, scalar);
                continue;
            }

            scalar -= 0x10000;
            AppendChar(ref hash, 0xD800 + (scalar >> 10));
            AppendChar(ref hash, 0xDC00 + (scalar & 0x3FF));
        }
    }

    internal static void AppendUInt64(ref ulong hash, ulong value)
    {
        for (var shift = 0; shift < 64; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= Prime;
        }
    }

    private static void AppendChar(ref ulong hash, uint character)
    {
        hash ^= (ushort)character;
        hash *= Prime;
    }
}

internal sealed class DivFingerprintProjectionState : IDisposable
{
    private int[] _activeResults = ArrayPool<int>.Shared.Rent(16);
    private DivResult[] _results = ArrayPool<DivResult>.Shared.Rent(64);
    private int _activeCount;
    private int _resultCount;
    private bool _disposed;

    internal ulong Fingerprint
    {
        get
        {
            var hash = DivFingerprint.OffsetBasis;
            for (var index = 0; index < _resultCount; index++)
            {
                DivFingerprint.AppendUInt64(ref hash, _results[index].IdHash);
                DivFingerprint.AppendUInt64(ref hash, _results[index].ClassHash);
                DivFingerprint.AppendUInt64(ref hash, _results[index].TextHash);
            }
            DivFingerprint.AppendUInt64(ref hash, (ulong)_resultCount);
            return hash;
        }
    }

    internal void Start(in Element element)
    {
        EnsureResultCapacity();
        EnsureActiveCapacity();
        var resultIndex = _resultCount++;
        _results[resultIndex] = new DivResult(
            element.TryGetAttribute("id"u8, out var id) ? DivFingerprint.HashUtf8(id) : DivFingerprint.OffsetBasis,
            element.TryGetAttribute("class"u8, out var classes)
                ? DivFingerprint.HashUtf8(classes)
                : DivFingerprint.OffsetBasis,
            DivFingerprint.OffsetBasis
        );
        _activeResults[_activeCount++] = resultIndex;
    }

    internal void Text(ReadOnlySpan<byte> utf8)
    {
        for (var index = 0; index < _activeCount; index++)
            DivFingerprint.AppendUtf8(ref _results[_activeResults[index]].TextHash, utf8);
    }

    internal void End() => _activeCount--;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ArrayPool<int>.Shared.Return(_activeResults);
        ArrayPool<DivResult>.Shared.Return(_results);
        _activeResults = [];
        _results = [];
        _activeCount = 0;
        _resultCount = 0;
    }

    private void EnsureActiveCapacity()
    {
        if (_activeCount < _activeResults.Length)
            return;
        var replacement = ArrayPool<int>.Shared.Rent(_activeResults.Length * 2);
        _activeResults.AsSpan(0, _activeCount).CopyTo(replacement);
        ArrayPool<int>.Shared.Return(_activeResults);
        _activeResults = replacement;
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

    private struct DivResult(ulong idHash, ulong classHash, ulong textHash)
    {
        internal ulong IdHash = idHash;
        internal ulong ClassHash = classHash;
        internal ulong TextHash = textHash;
    }
}
#endif
