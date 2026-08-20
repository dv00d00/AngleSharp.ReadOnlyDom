using System.Runtime.CompilerServices;

namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

internal partial class Utf8HtmlTokenizer<TResourceLimits>
    where TResourceLimits : struct, IResourceLimitPolicy
{
    // Threaded form of the raw-text / script-data content states. Bulk scans run inside the
    // labels, and a "</" stop resolves its end-tag candidate against _rawEndTag in-span with
    // one short, highly biased compare loop instead of surrendering '<', '/', and every name
    // byte to a per-byte dispatcher round-trip apiece (the correlated else-if chains and the
    // candidate ladder those round-trips walk are the dominant branch-mispredict source on
    // script-heavy inputs). The method exits - writing _state only then - at a transition out
    // of the region (an end tag matched), at a byte the per-byte machine must resolve ('\0',
    // '\r', '&' in captured RCDATA, "<!" in script data, unvalidated non-ASCII), or at the end
    // of the span. Candidates the span cannot resolve (letters running to the chunk boundary)
    // are declined - the scanner returns before their '<' - so the existing per-byte candidate
    // buffer carries them across the boundary unchanged; the escape-state family
    // (ScriptEscapeStart through ScriptDoubleEscapeEnd) likewise stays on the per-byte machine.
    private Int32 ScanRawTextContent<TMetrics, TTrust, TCapture>(
        ReadOnlySpan<Byte> utf8,
        Int64 sourceOffset,
        Boolean trackSourceRanges,
        Boolean yieldOnRequest
    )
        where TMetrics : struct, IStateMetricsPolicy
        where TTrust : struct, IInputTrustPolicy
        where TCapture : struct, IAttributeCapturePolicy
    {
        // Only the capturing instantiation reaches the sink mid-scan (EmitText); a callback
        // may request a yield there, so each emit that does not already exit the scanner is
        // followed by a yield check at the same observable boundary. The discarded
        // instantiation folds those checks away with the emits. FinishTag can also request a
        // yield, but every FinishTag here is immediately followed by a return, and the caller
        // checks the flag after any consumed bytes.
        var index = 0;
        var rawEndTag = _rawEndTag;
        ReadOnlySpan<Char> expected = rawEndTag is null ? "\0" : rawEndTag.AsSpan();
        var isRcData = rawEndTag is not null && rawEndTag.StartsWith("rcdata:", StringComparison.Ordinal);
        if (isRcData)
        {
            expected = expected[7..];
        }

        if (_state == State.ScriptData)
        {
            goto ScriptData;
        }
        if (expected.IsEmpty)
        {
            // The per-byte machine reaches RawEndTagName only through at least one letter, so
            // an empty expected name (reachable through SetMode) never matches; "\0" cannot.
            // Script data keeps the empty name: its candidate state is entered before the
            // first letter, so "</>" does match an empty expectation there.
            expected = "\0";
        }
        goto RawText;

        ScriptData:
        while (true)
        {
            var remaining = utf8[index..];
            var run = TCapture.Enabled
                ? IndexOfCaptureStop<TTrust>(remaining, RawTextTerminators, RawTextArbitraryAllowed)
                : Utf8DiscardedTextScanner.IndexOfScriptDataStop(remaining);
            if (run < 0)
            {
                RecordState<TMetrics>((Int32)State.ScriptData, remaining.Length);
                if (TCapture.Enabled)
                {
                    EmitText(remaining);
                    EmitRawText(sourceOffset + index, remaining, Utf8HtmlTextType.ScriptData);
                }
                return utf8.Length;
            }
            if (run > 0)
            {
                RecordState<TMetrics>((Int32)State.ScriptData, run);
                index += run;
                if (TCapture.Enabled)
                {
                    EmitText(remaining[..run]);
                    EmitRawText(sourceOffset + index - run, remaining[..run], Utf8HtmlTextType.ScriptData);
                    if (yieldOnRequest && _yieldRequested)
                    {
                        return index;
                    }
                }
            }
            if (utf8[index] != (Byte)'<' || (UInt32)(index + 1) >= (UInt32)utf8.Length)
            {
                // '\0', '\r', or unvalidated non-ASCII (capture stops), or a trailing '<'
                // the per-byte machine must hold across the chunk boundary.
                return index;
            }
            var next = utf8[index + 1];
            if (next == (Byte)'/')
            {
                var resolution = ScanEndTagCandidate(utf8, index + 2, expected, out var matched);
                if (resolution < 0)
                {
                    // Letters ran to the end of the span: the per-byte machine buffers the
                    // candidate and decides when the next chunk arrives.
                    return index;
                }
                if (matched)
                {
                    EndRawText(sourceOffset + index);
                    return CompleteScannedRawEndTag<TMetrics>(
                        utf8,
                        index,
                        resolution,
                        State.ScriptEndTagName,
                        sourceOffset,
                        trackSourceRanges
                    );
                }
                // Resolved mismatch: "</" and its letters are ordinary script text.
                RecordState<TMetrics>((Int32)State.ScriptData, resolution - index);
                if (TCapture.Enabled)
                {
                    EmitText(utf8[index..resolution]);
                    EmitRawText(sourceOffset + index, utf8[index..resolution], Utf8HtmlTextType.ScriptData);
                    if (yieldOnRequest && _yieldRequested)
                    {
                        return resolution;
                    }
                }
                index = resolution;
                continue;
            }
            if (next == (Byte)'!')
            {
                // Possible "<!--" escape entry; the per-byte machine drives the escape family.
                return index;
            }
            // A lone '<' cannot change the tokenizer state; it is ordinary script text.
            RecordState<TMetrics>((Int32)State.ScriptData, 1);
            index++;
            if (TCapture.Enabled)
            {
                EmitText("<"u8);
                EmitRawText(sourceOffset + index - 1, "<"u8, Utf8HtmlTextType.ScriptData);
                if (yieldOnRequest && _yieldRequested)
                {
                    return index;
                }
            }
        }

        RawText:
        while (true)
        {
            var remaining = utf8[index..];
            var run = TCapture.Enabled
                ? isRcData
                    ? IndexOfCaptureStop<TTrust>(remaining, DataTextTerminators, DataTextArbitraryAllowed)
                    : IndexOfCaptureStop<TTrust>(remaining, RawTextTerminators, RawTextArbitraryAllowed)
                : Utf8DiscardedTextScanner.IndexOfRawTextStop(remaining);
            if (run < 0)
            {
                RecordState<TMetrics>((Int32)State.RawText, remaining.Length);
                if (TCapture.Enabled)
                {
                    EmitText(remaining);
                    EmitRawText(
                        sourceOffset + index,
                        remaining,
                        isRcData ? Utf8HtmlTextType.RcData : Utf8HtmlTextType.RawText
                    );
                }
                return utf8.Length;
            }
            if (run > 0)
            {
                RecordState<TMetrics>((Int32)State.RawText, run);
                index += run;
                if (TCapture.Enabled)
                {
                    EmitText(remaining[..run]);
                    EmitRawText(
                        sourceOffset + index - run,
                        remaining[..run],
                        isRcData ? Utf8HtmlTextType.RcData : Utf8HtmlTextType.RawText
                    );
                    if (yieldOnRequest && _yieldRequested)
                    {
                        return index;
                    }
                }
            }
            if (utf8[index] != (Byte)'<' || (UInt32)(index + 1) >= (UInt32)utf8.Length)
            {
                // '\0', '\r', '&' (captured RCDATA), or unvalidated non-ASCII (capture
                // stops), or a trailing '<' the per-byte machine must hold.
                return index;
            }
            if (utf8[index + 1] == (Byte)'/')
            {
                var resolution = ScanEndTagCandidate(utf8, index + 2, expected, out var matched);
                if (resolution < 0)
                {
                    return index;
                }
                if (matched)
                {
                    EndRawText(sourceOffset + index);
                    return CompleteScannedRawEndTag<TMetrics>(
                        utf8,
                        index,
                        resolution,
                        State.RawEndTagName,
                        sourceOffset,
                        trackSourceRanges
                    );
                }
                RecordState<TMetrics>((Int32)State.RawText, resolution - index);
                if (TCapture.Enabled)
                {
                    EmitText(utf8[index..resolution]);
                    EmitRawText(
                        sourceOffset + index,
                        utf8[index..resolution],
                        isRcData ? Utf8HtmlTextType.RcData : Utf8HtmlTextType.RawText
                    );
                    if (yieldOnRequest && _yieldRequested)
                    {
                        return resolution;
                    }
                }
                index = resolution;
                continue;
            }
            RecordState<TMetrics>((Int32)State.RawText, 1);
            index++;
            if (TCapture.Enabled)
            {
                EmitText("<"u8);
                EmitRawText(
                    sourceOffset + index - 1,
                    "<"u8,
                    isRcData ? Utf8HtmlTextType.RcData : Utf8HtmlTextType.RawText
                );
                if (yieldOnRequest && _yieldRequested)
                {
                    return index;
                }
            }
        }
    }

    /// <summary>
    /// Resolves an end-tag candidate whose name starts at <paramref name="nameStart"/> (just
    /// past "&lt;/"). Returns the index of the first non-letter byte - the resolution point -
    /// or -1 when letters run to the end of the span, in which case the caller hands the whole
    /// candidate to the per-byte machine, whose candidate buffer carries it across the chunk
    /// boundary. <paramref name="matched"/> reports, for a resolved candidate, whether the
    /// letter run equals the expected end-tag name and the resolution byte is a tag delimiter -
    /// the exact condition the per-byte ladder tests.
    /// </summary>
    private static Int32 ScanEndTagCandidate(
        ReadOnlySpan<Byte> utf8,
        Int32 nameStart,
        ReadOnlySpan<Char> expected,
        out Boolean matched
    )
    {
        var position = nameStart;
        var expectedIndex = 0;
        var equal = true;
        while (true)
        {
            if ((UInt32)position >= (UInt32)utf8.Length)
            {
                matched = false;
                return -1;
            }
            var value = utf8[position];
            if (!IsAsciiLetter(value))
            {
                matched = equal && expectedIndex == expected.Length && IsTagDelimiter(value);
                return position;
            }
            equal =
                equal
                && expectedIndex < expected.Length
                && AsciiLower(value) == AsciiLower((Byte)expected[expectedIndex]);
            expectedIndex++;
            position++;
        }
    }

    /// <summary>
    /// Performs the per-byte ladder's success arm for an end-tag candidate the threaded scanner
    /// matched in-span: "&lt;/" at <paramref name="candidateStart"/>, the name up to
    /// <paramref name="delimiterIndex"/>, and the delimiter byte there. Returns the count of
    /// bytes consumed through the delimiter.
    /// </summary>
    private Int32 CompleteScannedRawEndTag<TMetrics>(
        ReadOnlySpan<Byte> utf8,
        Int32 candidateStart,
        Int32 delimiterIndex,
        State matchState,
        Int64 sourceOffset,
        Boolean trackSourceRanges
    )
        where TMetrics : struct, IStateMetricsPolicy
    {
        if (TMetrics.Enabled)
        {
            RecordRawEndTagMatch(matchState, delimiterIndex - candidateStart - 2);
        }
        Clear(_name);
        _tagNameIdentityCache.Reset();
        AppendTagName(utf8[(candidateStart + 2)..delimiterIndex]);
        _isEndTag = true;
        _rawEndTag = null;
        var delimiter = utf8[delimiterIndex];
        var index = delimiterIndex + 1;
        if (trackSourceRanges)
        {
            _currentTagSourceOffset = sourceOffset + candidateStart;
            _currentSourceOffset = sourceOffset + index;
        }
        if (delimiter == (Byte)'>')
        {
            // FinishTagCore rewrites any non-text state to Data; the candidate state mirrors
            // the state the per-byte machine would have held at this transition.
            _state = matchState;
            FinishTag(selfClosing: false);
            return index;
        }
        if (delimiter == (Byte)'/')
        {
            _state = State.SelfClosingStartTag;
            return index;
        }
        if (delimiter == (Byte)'\r')
        {
            _pendingCarriageReturn = true;
        }
        _state = State.BeforeAttributeName;
        return index;
    }

    // Metrics for an in-span end-tag candidate match, kept out of the scan loop; records the
    // states the surrendered per-byte path would have walked.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RecordRawEndTagMatch(State matchState, Int32 nameLength)
    {
        if (matchState == State.ScriptEndTagName)
        {
            _stateMetrics!.Record((Int32)State.ScriptData, 1);
            _stateMetrics.Record((Int32)State.ScriptLessThan, 1);
            _stateMetrics.Record((Int32)State.ScriptEndTagName, nameLength + 1);
        }
        else
        {
            _stateMetrics!.Record((Int32)State.RawText, 1);
            _stateMetrics.Record((Int32)State.RawLessThan, 1);
            _stateMetrics.Record((Int32)State.RawEndTagOpen, 1);
            _stateMetrics.Record((Int32)State.RawEndTagName, nameLength);
        }
    }
}
