#pragma warning disable CS1591 // Experimental implementation detail; shape is intentionally unsettled.

using System.Buffers;
using System.Text;

namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

public enum Utf8InputContract : byte
{
    /// <summary>Validates arbitrary input and replaces malformed UTF-8 with U+FFFD.</summary>
    ArbitraryBytes,

    /// <summary>
    /// Skips bulk validation because the producer guarantees well-formed UTF-8. Supplying malformed input violates the
    /// contract and produces unspecified token payloads.
    /// </summary>
    WellFormedUtf8,
}

/// <summary>
/// Owns UTF-8 framing, validation, malformed-input replacement, and source-byte accounting before bytes reach the
/// HTML tokenizer state machine.
/// </summary>
internal struct Utf8InputNormalizer
{
    // Validation runs in windows so that bulk text between tags can reach the tokenizer through the
    // fused arbitrary-text path instead of being swallowed into a chunk-sized validated prefix. The
    // window doubles while the tokenizer stays in markup (converging to whole-chunk validation on
    // markup-dense documents) and resets whenever a long fused text run shows validation is being
    // skipped profitably.
    private const Int32 MinimumValidationWindow = 128;
    private const Int32 MaximumValidationWindow = 4096;
    private const Int32 FusedRunWindowReset = 128;

    private readonly Int64 _maximumInputBytesAllowed;
    private readonly Utf8InputContract _contract;
    private UInt32 _carry;
    private Int64 _bytesConsumed;
    private Int32 _validatedPrefixLength;
    private Int32 _validationWindow;
    private Byte _carryLength;

    internal Utf8InputNormalizer(Int64 maximumInputBytesAllowed, Utf8InputContract contract)
    {
        _maximumInputBytesAllowed = maximumInputBytesAllowed;
        _contract = contract;
    }

    internal readonly Int64 BytesConsumed => _bytesConsumed;

    internal Int32 Write(Utf8HtmlTokenizer tokenizer, ReadOnlySpan<Byte> utf8, Boolean yieldOnRequest)
    {
        var previousBytesConsumed = _bytesConsumed;
        var observedInputBytes = SaturatingAdd(_bytesConsumed, utf8.Length);
        if (observedInputBytes > _maximumInputBytesAllowed)
        {
            throw new HtmlStreamingLimitExceededException(
                HtmlStreamingLimit.InputBytes,
                _maximumInputBytesAllowed,
                observedInputBytes
            );
        }

        _bytesConsumed = observedInputBytes;
        var index = 0;
        if (_carryLength != 0)
        {
            index =
                _contract == Utf8InputContract.WellFormedUtf8
                    ? DrainWellFormedCarry(tokenizer, utf8, yieldOnRequest)
                    : DrainCarry(tokenizer, utf8, yieldOnRequest);
            if (_carryLength != 0)
            {
                if (yieldOnRequest && tokenizer.IsYieldRequested)
                {
                    _bytesConsumed = SaturatingAdd(previousBytesConsumed, index);
                }
                return index;
            }
            if (yieldOnRequest && tokenizer.IsYieldRequested)
            {
                _bytesConsumed = SaturatingAdd(previousBytesConsumed, index);
                return index;
            }
        }

        if (_contract == Utf8InputContract.WellFormedUtf8)
        {
            return WriteWellFormed(tokenizer, utf8, index, previousBytesConsumed, yieldOnRequest);
        }

        // The ASCII fast path lets the tokenizer's own state machine consume unvalidated input and
        // stop exactly at the first non-ASCII byte, so validation runs only over non-ASCII runs.
        // It cannot be used while start-tag source ranges are observed, because a partially
        // consumed span would be observed again on re-entry.
        var asciiFastPath = !tokenizer.TracksStartTagSourceRanges;

        while (index < utf8.Length)
        {
            if (_validatedPrefixLength != 0)
            {
                var available = Math.Min(_validatedPrefixLength, utf8.Length - index);
                var consumed = tokenizer.WriteTrustedUtf8(utf8.Slice(index, available), yieldOnRequest);
                _validatedPrefixLength -= consumed;
                index += consumed;
                if (consumed != available)
                {
                    _bytesConsumed = SaturatingAdd(previousBytesConsumed, index);
                    return index;
                }
                if (yieldOnRequest && tokenizer.IsYieldRequested)
                {
                    _bytesConsumed = SaturatingAdd(previousBytesConsumed, index);
                    return index;
                }
                continue;
            }

            if (asciiFastPath)
            {
                var consumed = tokenizer.WriteArbitraryAscii(utf8[index..], yieldOnRequest);
                if (consumed > 0)
                {
                    index += consumed;
                    if (consumed >= FusedRunWindowReset)
                    {
                        _validationWindow = 0;
                    }
                    if (yieldOnRequest && tokenizer.IsYieldRequested)
                    {
                        _bytesConsumed = SaturatingAdd(previousBytesConsumed, index);
                        return index;
                    }
                    continue;
                }
                // Zero bytes consumed: the cursor is a non-ASCII byte for the window below.
            }

            var remainingUtf8 = NextValidationWindow(utf8[index..]);
            var windowEndsChunk = remainingUtf8.Length == utf8.Length - index;
            var nonAscii = remainingUtf8.IndexOfAnyExceptInRange((Byte)0x00, (Byte)0x7F);
            if (nonAscii < 0)
            {
                nonAscii = remainingUtf8.Length;
            }
            if (nonAscii != 0)
            {
                _validatedPrefixLength = nonAscii;
                continue;
            }

            // A truncated-looking tail means "wait for the next chunk" only at a true chunk end.
            // A mid-chunk window was already cut on a boundary that cannot split a valid sequence,
            // so any such tail is guaranteed malformed and must be replaced in stream order now
            // (the malformed writer's need-more-data branch does exactly that) instead of being
            // carried past the bytes that follow it.
            var completeLength = windowEndsChunk ? CompleteUtf8PrefixLength(remainingUtf8) : remainingUtf8.Length;
            if (completeLength != 0 && System.Text.Unicode.Utf8.IsValid(remainingUtf8[..completeLength]))
            {
                _validatedPrefixLength = completeLength;
                continue;
            }

            if (completeLength != 0)
            {
                var malformedConsumed = WriteMalformedUtf8(tokenizer, remainingUtf8[..completeLength], yieldOnRequest);
                index += malformedConsumed;
                if (malformedConsumed != completeLength || (yieldOnRequest && tokenizer.IsYieldRequested))
                {
                    _bytesConsumed = SaturatingAdd(previousBytesConsumed, index);
                    return index;
                }

                if (completeLength != remainingUtf8.Length)
                {
                    SaveCarry(remainingUtf8[completeLength..]);
                    index += remainingUtf8.Length - completeLength;
                }
                continue;
            }

            SaveCarry(remainingUtf8);
            index += remainingUtf8.Length;
        }

        return index;
    }

    private ReadOnlySpan<Byte> NextValidationWindow(ReadOnlySpan<Byte> remaining)
    {
        var window = _validationWindow == 0 ? MinimumValidationWindow : _validationWindow;
        _validationWindow = Math.Min(window * 2, MaximumValidationWindow);
        if (window >= remaining.Length)
        {
            return remaining;
        }

        // Never let a mid-chunk window split a possibly-valid UTF-8 sequence: shrink to the last
        // complete boundary (the tail rejoins the same chunk on the next iteration). One trim can
        // expose a new truncated-looking tail, but that tail is then guaranteed malformed because
        // the byte after the cut is a lead rather than a continuation — the write loop replaces it
        // in place rather than treating it as a chunk-end carry.
        var complete = CompleteUtf8PrefixLength(remaining[..window]);
        return complete > 0 ? remaining[..complete] : remaining;
    }

    private Int32 DrainWellFormedCarry(Utf8HtmlTokenizer tokenizer, ReadOnlySpan<Byte> utf8, Boolean yieldOnRequest)
    {
        var expectedLength = Utf8SequenceLength((Byte)_carry);
        var index = 0;
        while (_carryLength < expectedLength && index < utf8.Length)
        {
            AppendCarry(utf8[index++]);
        }

        if (_carryLength == expectedLength)
        {
            Span<Byte> scalar = stackalloc Byte[4];
            CopyCarryTo(scalar);
            tokenizer.WriteTrustedUtf8(scalar[..expectedLength], yieldOnRequest);
            ClearCarry();
        }

        return index;
    }

    private Int32 DrainCarry(Utf8HtmlTokenizer tokenizer, ReadOnlySpan<Byte> utf8, Boolean yieldOnRequest)
    {
        Span<Byte> candidate = stackalloc Byte[4];
        var index = 0;
        while (_carryLength != 0)
        {
            CopyCarryTo(candidate);
            var status = Rune.DecodeFromUtf8(candidate[.._carryLength], out _, out var consumed);
            if (status == OperationStatus.Done)
            {
                tokenizer.WriteTrustedUtf8(candidate[..consumed], yieldOnRequest);
                ClearCarry();
                return index;
            }
            if (status == OperationStatus.InvalidData)
            {
                tokenizer.WriteTrustedUtf8("\uFFFD"u8, yieldOnRequest);
                ShiftCarry(Math.Max(consumed, 1));
                if (yieldOnRequest && tokenizer.IsYieldRequested)
                {
                    return index;
                }
                continue;
            }
            if (index == utf8.Length)
            {
                break;
            }

            AppendCarry(utf8[index++]);
        }

        return index;
    }

    internal void Complete(Utf8HtmlTokenizer tokenizer)
    {
        if (_carryLength == 0)
        {
            return;
        }

        tokenizer.WriteTrustedUtf8("\uFFFD"u8, yieldOnRequest: false);
        ClearCarry();
    }

    private Int32 WriteWellFormed(
        Utf8HtmlTokenizer tokenizer,
        ReadOnlySpan<Byte> utf8,
        Int32 index,
        Int64 previousBytesConsumed,
        Boolean yieldOnRequest
    )
    {
        var remaining = utf8[index..];
        var completeLength = CompleteUtf8PrefixLength(remaining);
        if (completeLength != 0)
        {
            var consumed = tokenizer.WriteTrustedUtf8(remaining[..completeLength], yieldOnRequest);
            index += consumed;
            if (consumed != completeLength)
            {
                _bytesConsumed = SaturatingAdd(previousBytesConsumed, index);
                return index;
            }
        }

        if (completeLength != remaining.Length)
        {
            SaveCarry(remaining[completeLength..]);
            index = utf8.Length;
        }

        return index;
    }

    private static Int32 WriteMalformedUtf8(
        Utf8HtmlTokenizer tokenizer,
        ReadOnlySpan<Byte> utf8,
        Boolean yieldOnRequest
    )
    {
        var index = 0;
        var validStart = 0;
        while (index < utf8.Length)
        {
            var status = Rune.DecodeFromUtf8(utf8[index..], out _, out var consumed);
            if (status == OperationStatus.Done)
            {
                index += consumed;
                continue;
            }

            if (index != validStart)
            {
                var validConsumed = tokenizer.WriteTrustedUtf8(
                    utf8.Slice(validStart, index - validStart),
                    yieldOnRequest
                );
                validStart += validConsumed;
                if (validStart != index || (yieldOnRequest && tokenizer.IsYieldRequested))
                {
                    return validStart;
                }
            }

            tokenizer.WriteTrustedUtf8("\uFFFD"u8, yieldOnRequest);
            if (status == OperationStatus.NeedMoreData)
            {
                return utf8.Length;
            }

            index += Math.Max(consumed, 1);
            validStart = index;
        }

        if (index != validStart)
        {
            validStart += tokenizer.WriteTrustedUtf8(utf8[validStart..], yieldOnRequest);
        }

        return validStart;
    }

    private void AppendCarry(Byte value)
    {
        _carry |= (UInt32)value << (_carryLength * 8);
        _carryLength++;
    }

    private readonly void CopyCarryTo(Span<Byte> destination)
    {
        for (var index = 0; index < _carryLength; index++)
        {
            destination[index] = (Byte)(_carry >> (index * 8));
        }
    }

    private void SaveCarry(ReadOnlySpan<Byte> value)
    {
        _carry = 0;
        _carryLength = 0;
        foreach (var item in value)
        {
            AppendCarry(item);
        }
    }

    private void ShiftCarry(Int32 consumed)
    {
        _carry >>= consumed * 8;
        _carryLength -= (Byte)consumed;
    }

    private void ClearCarry()
    {
        _carry = 0;
        _carryLength = 0;
    }

    private static Int32 CompleteUtf8PrefixLength(ReadOnlySpan<Byte> value)
    {
        if (value.IsEmpty)
        {
            return 0;
        }

        var lead = value.Length - 1;
        while (lead > 0 && value[lead] is >= 0x80 and <= 0xBF && value.Length - lead < 4)
        {
            lead--;
        }

        var expected = Utf8SequenceLength(value[lead]);
        return expected > 1 && value.Length - lead < expected ? lead : value.Length;
    }

    private static Int32 Utf8SequenceLength(Byte lead) =>
        lead switch
        {
            < 0x80 => 1,
            < 0xE0 => 2,
            < 0xF0 => 3,
            < 0xF8 => 4,
            _ => 1,
        };

    private static Int64 SaturatingAdd(Int64 left, Int64 right) =>
        left > Int64.MaxValue - right ? Int64.MaxValue : left + right;
}
