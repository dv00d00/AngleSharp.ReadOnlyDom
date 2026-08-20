using System.Buffers;
using System.Runtime.CompilerServices;

namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

internal partial class Utf8HtmlTokenizer<TResourceLimits>
    where TResourceLimits : struct, IResourceLimitPolicy
{
    private const Int32 AttributeIndexPromotionThreshold = 16;
    private const UInt64 IframeKey = 0x000000001CBB9A4AUL;
    private const UInt64 NoEmbedKey = 0x00000004E8A91D49UL;
    private const UInt64 NoFramesKey = 0x0000009D17734958UL;
    private const UInt64 PlaintextKey = 0x000015899D3CABB9UL;
    private const UInt64 ScriptKey = 0x00000000308BBAB9UL;
    private const UInt64 StyleKey = 0x00000000018CFA2AUL;
    private const UInt64 TextareaKey = 0x000000CABB935D46UL;
    private const UInt64 TitleKey = 0x000000000197662AUL;
    private const UInt64 XmpKey = 0x0000000000007655UL;

    private enum AttributeCapture : byte
    {
        Undecided,
        Capture,
        Discard,
        Duplicate,
    }

    private static readonly SearchValues<Byte> TagNameTerminators = SearchValues.Create("\0\t\n\f\r />"u8);
    private static readonly SearchValues<Byte> TagNameArbitraryAllowed = CreateArbitraryAllowed("\0\t\n\f\r />"u8);

    // Every tag-name terminator is a byte value below 64, so a single 64-bit mask plus a
    // shift-and-test classifies a byte in two ALU instructions — the same shape as the
    // scalar loop in lol-html's tag_name_state, that parser's single hottest function.
    private const UInt64 TagNameTerminatorMask =
        1UL << '\0' | 1UL << '\t' | 1UL << '\n' | 1UL << '\f' | 1UL << '\r' | 1UL << ' ' | 1UL << '/' | 1UL << '>';

    // Names still unterminated after this many bytes fall through to the vectorized scan.
    private const Int32 TagNameScalarScanLimit = 16;

    /// <summary>
    /// Tag-name variant of <see cref="IndexOfCaptureStop{TTrust}"/> with identical
    /// classification (same terminator set; the untrusted instantiation additionally stops at
    /// non-ASCII), but the first <see cref="TagNameScalarScanLimit"/> bytes are scanned with a
    /// plain byte loop. Typical tag names are 3-6 bytes, for which the vectorized searcher's
    /// per-call setup costs more than the entire scan; only a name that outruns the peel pays
    /// for the vector machinery, on the bytes past it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Int32 IndexOfTagNameStop<TTrust>(ReadOnlySpan<Byte> utf8)
        where TTrust : struct, IInputTrustPolicy
    {
        var scan = utf8.Length <= TagNameScalarScanLimit ? utf8 : utf8[..TagNameScalarScanLimit];
        var index = 0;
        while ((UInt32)index < (UInt32)scan.Length)
        {
            UInt32 value = scan[index];
            if (value < 64 && (TagNameTerminatorMask & (1UL << (Int32)value)) != 0)
            {
                return index;
            }
            if (TTrust.StopAtNonAscii && value >= 0x80)
            {
                return index;
            }
            index++;
        }
        if (index >= utf8.Length)
        {
            return -1;
        }
        var stop = IndexOfCaptureStop<TTrust>(utf8[index..], TagNameTerminators, TagNameArbitraryAllowed);
        return stop < 0 ? -1 : index + stop;
    }

    private static readonly SearchValues<Byte> AttributeNameTerminators = SearchValues.Create("\0\t\n\f\r /=>"u8);
    private static readonly SearchValues<Byte> DiscardedAttributeNameTerminators = SearchValues.Create(
        "\t\n\f\r /=>"u8
    );

    private const UInt64 DiscardedAttributeNameTerminatorMask =
        1UL << '\t' | 1UL << '\n' | 1UL << '\f' | 1UL << '\r' | 1UL << ' ' | 1UL << '/' | 1UL << '=' | 1UL << '>';

    // Wider than the tag-name window: a fifth of linkedin's attribute names are 17-32 bytes.
    private const Int32 DiscardedAttributeNameScalarScanLimit = 32;

    /// <summary>
    /// <see cref="IndexOfTagNameStop{TTrust}"/> for discarded attribute names, same classification as
    /// <see cref="DiscardedAttributeNameTerminators"/>. Names are 6-10 bytes across this corpus, where
    /// the searcher's per-call setup costs more than the scan; past the window it takes over, so a
    /// pathological name is not scanned a byte at a time. Rejected in 2026-08-09 on retired
    /// instructions (+0.41%) - the wrong meter, the setup is cheap in instructions and dear in cycles.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Int32 IndexOfDiscardedAttributeNameStop(ReadOnlySpan<Byte> utf8)
    {
        var scan =
            utf8.Length <= DiscardedAttributeNameScalarScanLimit ? utf8 : utf8[..DiscardedAttributeNameScalarScanLimit];
        var index = 0;
        while ((UInt32)index < (UInt32)scan.Length)
        {
            UInt32 value = scan[index];
            if (value < 64 && (DiscardedAttributeNameTerminatorMask & (1UL << (Int32)value)) != 0)
            {
                return index;
            }
            index++;
        }
        if (index >= utf8.Length)
        {
            return -1;
        }
        var beyond = utf8[index..].IndexOfAny(DiscardedAttributeNameTerminators);
        return beyond < 0 ? -1 : index + beyond;
    }

    private static readonly SearchValues<Byte> AttributeNameArbitraryAllowed = CreateArbitraryAllowed(
        "\0\t\n\f\r /=>"u8
    );

    // '&' never terminates a captured value scan: a character reference inside an attribute
    // value cannot affect tokenization, so references are decoded once over the contiguous
    // buffered value when the attribute commits instead of per byte during the scan.
    private static readonly SearchValues<Byte> DoubleQuotedAttributeValueTerminators = SearchValues.Create("\"\0\r"u8);
    private static readonly SearchValues<Byte> SingleQuotedAttributeValueTerminators = SearchValues.Create("'\0\r"u8);
    private static readonly SearchValues<Byte> UnquotedAttributeValueTerminators = SearchValues.Create(
        "\0>\t\n\f\r "u8
    );
    private static readonly SearchValues<Byte> DiscardedUnquotedAttributeValueTerminators = SearchValues.Create(
        ">\t\n\f\r "u8
    );
    private static readonly SearchValues<Byte> DoubleQuotedAttributeValueArbitraryAllowed = CreateArbitraryAllowed(
        "\"\0\r"u8
    );
    private static readonly SearchValues<Byte> SingleQuotedAttributeValueArbitraryAllowed = CreateArbitraryAllowed(
        "'\0\r"u8
    );
    private static readonly SearchValues<Byte> UnquotedAttributeValueArbitraryAllowed = CreateArbitraryAllowed(
        "\0>\t\n\f\r "u8
    );

    private void BeginTag(Boolean isEndTag, Byte firstByte)
    {
        EndRawText(_lastLessThanSourceOffset);
        _isEndTag = isEndTag;
        if (_startTagSourceRangeSink is not null)
        {
            _currentTagSourceOffset = _lastLessThanSourceOffset;
        }
        _startTagEmitted = false;
        _captureStartTagAttributes = false;
        Clear(_name);
        Clear(_attributeName);
        Clear(_attributeValue);
        Clear(_seenAttributeNames);
        _seenCompactAttributeNames?.Reset();
        Utf8AttributeNameIndex.Reset(ref _seenAttributeIndex);
        _seenFallbackAttributeCount = 0;
        _tagNameIdentityCache.Reset();
        _attributeNameIdentityCache.Reset();
        _attributeCapture = AttributeCapture.Undecided;
        AppendTagName(firstByte);
        _state = State.TagName;
    }

    private void EmitTagStart()
    {
        if (_startTagEmitted || _isEndTag)
        {
            return;
        }

        _captureStartTagAttributes = (_sink.StartTag(CurrentTagName()) & Utf8HtmlStartTagCapture.Attributes) != 0;
        if (_captureStartTagAttributes)
        {
            // One virtual fetch per captured tag; DecideAttributeCapture then pre-filters every
            // attribute of the tag against this snapshot without calling back into the sink.
            _attributeNameFilter = _sink.StartTagAttributeFilter;
            _attributeNameLengths = _sink.StartTagAttributeNameLengths;
        }
        _startTagEmitted = true;
    }

    private void DecideAttributeCapture()
    {
        if (_attributeCapture != AttributeCapture.Undecided)
        {
            return;
        }

        if (!_captureStartTagAttributes || _isEndTag)
        {
            _attributeCapture = AttributeCapture.Discard;
            return;
        }
        EmitTagStart();
        // Query-directed pre-filter: the sink published a bloom over the semantic hashes of every
        // attribute name it can still want on this tag (IUtf8HtmlTokenSink.StartTagAttributeFilter).
        // A missed bit proves the sink rejects this name, so the fast path skips Utf8HtmlName
        // materialization, the virtual WantsAttribute call, and duplicate tracking. Skipping the
        // duplicate tracking is safe: suppression is only observable for emitted attributes, and a
        // semantically equal respelling folds to the same hash and therefore the same verdict, so
        // every occurrence of a filter-rejected name is rejected here — a rejected occurrence can
        // never be the first-seen occurrence of an accepted name. Hash false positives merely fall
        // through to the exact path below, which behaves exactly as the unfiltered tokenizer.
        var filter = _attributeNameFilter;
        // Length first: it is a shift and a test against a value already in hand, whereas the hash
        // below is a serial (hash ^ byte) * prime chain over every byte of the name. Real markup
        // rejects on length far more often than it collides - a[href] wants only 4-byte names while
        // linkedin's names average 8.4 bytes with a fifth of them data-* - so paying the hash first
        // spent the whole chain to learn what one bit test already knew. The safety argument is the
        // filter's, verbatim and spelling-independent: this rejects every occurrence of a name whose
        // length cannot be wanted, so a rejected occurrence can never be the first-seen occurrence of
        // an accepted name, and duplicate tracking stays observationally identical.
        var lengths = _attributeNameLengths;
        if (lengths != UInt64.MaxValue && (lengths & (1UL << Math.Min(_attributeName.WrittenCount, 63))) == 0)
        {
            _attributeCapture = AttributeCapture.Discard;
            return;
        }
        if (
            filter != UInt64.MaxValue
            && (
                filter
                & Utf8NameHash.AttributeFilterBit(_attributeNameIdentityCache.GetOrCompute(_attributeName.WrittenSpan))
            ) == 0
        )
        {
            _attributeCapture = AttributeCapture.Discard;
            return;
        }
        var name = CurrentAttributeName();
        var capture = _sink.WantsAttribute(name);
        _attributeCapture =
            !TryAddSeenAttribute(name) ? AttributeCapture.Duplicate
            : capture ? AttributeCapture.Capture
            : AttributeCapture.Discard;
    }

    private void CommitAttribute()
    {
        // FinishTag calls this on every tag close whether or not an attribute is pending;
        // the commit path's struct locals must stay out of this frame so the nothing-pending
        // exit does not pay their stack clearing.
        if (_attributeName.WrittenCount != 0)
        {
            CommitPendingAttribute();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CommitPendingAttribute()
    {
        if (_isEndTag)
        {
            Clear(_attributeName);
            Clear(_attributeValue);
            _attributeCapture = AttributeCapture.Undecided;
            return;
        }
        DecideAttributeCapture();
        var name = CurrentAttributeName();
        if (_attributeCapture != AttributeCapture.Duplicate)
        {
            if (_attributeCapture == AttributeCapture.Capture)
            {
                // The value goes out raw: references in attribute values cannot affect
                // tokenization, and most captured values are never read, so the consumer
                // that actually reads one runs Utf8AttributeValueDecoder over it instead.
                _sink.Attribute(name, WrittenSpan(_attributeValue), !IsNotConsumingCharacterReferences);
            }
        }
        Clear(_attributeName);
        Clear(_attributeValue);
        _attributeNameIdentityCache.Reset();
        _attributeCapture = AttributeCapture.Undecided;
    }

    private Boolean TryAddSeenAttribute(Utf8HtmlName name)
    {
        if (name.Verbatim.Length <= 12 && name.Verbatim.IndexOf((Byte)'-') < 0 && name.TryGetCompactKey(out var key))
        {
            return (_seenCompactAttributeNames ??= new Utf8CompactAttributeNameSet()).TryAdd(key);
        }

        if (HasSeenFallbackAttribute(name))
        {
            return false;
        }

        var seenNames = _seenAttributeNames ??= new Utf8TokenBuffer(128);
        var nameOffset = seenNames.WrittenCount;
        Append(seenNames, name.Verbatim);
        Append(seenNames, (Byte)0);
        if (_seenAttributeIndex is not null)
        {
            Utf8AttributeNameIndex.Add(ref _seenAttributeIndex, name.SemanticHash, nameOffset);
        }
        _seenFallbackAttributeCount++;
        return true;
    }

    private Boolean HasSeenFallbackAttribute(Utf8HtmlName name)
    {
        var index = _seenAttributeIndex;
        if (index is not null)
        {
            return Utf8AttributeNameIndex.Contains(index, name, WrittenSpan(_seenAttributeNames));
        }

        var seen = WrittenSpan(_seenAttributeNames);
        while (!seen.IsEmpty)
        {
            var end = seen.IndexOf((Byte)0);
            if (end < 0)
            {
                return false;
            }

            if (name.SemanticEquals(seen[..end]))
            {
                return true;
            }

            seen = seen.Slice(end + 1);
        }

        if (_seenFallbackAttributeCount >= AttributeIndexPromotionThreshold)
        {
            Utf8AttributeNameIndex.Initialize(
                ref _seenAttributeIndex,
                WrittenSpan(_seenAttributeNames),
                _seenFallbackAttributeCount
            );
        }
        return false;
    }

    private void FinishTag(Boolean selfClosing)
    {
        if (_seenAttributeIndex is null && _seenFallbackAttributeCount < AttributeIndexPromotionThreshold)
        {
            CommitAttribute();
            FinishTagCore(selfClosing);
            _seenFallbackAttributeCount = 0;
            return;
        }

        try
        {
            CommitAttribute();
            FinishTagCore(selfClosing);
        }
        finally
        {
            Utf8AttributeNameIndex.Reset(ref _seenAttributeIndex);
            _seenFallbackAttributeCount = 0;
        }
    }

    private void FinishTagCore(Boolean selfClosing)
    {
        if (_isEndTag)
        {
            if (_startTagSourceRangeSink?.WantsEndTagSourceRanges == true)
                _startTagSourceRangeSink.EndTagSourceRange(_currentTagSourceOffset, _currentSourceOffset);
            _sink.EndTag(CurrentTagName());
            RefreshCapture();
            _rawEndTag = null;
        }
        else
        {
            EmitTagStart();
            _startTagSourceRangeSink?.StartTagSourceRange(_currentTagSourceOffset, _currentSourceOffset);
            _sink.StartTagEnd(selfClosing);
            RefreshCapture();
            // In HTML, the trailing solidus does not make a non-void element self-closing.
            // Tree construction controls the mode in the DOM path; the standalone path must
            // therefore still infer text modes for e.g. <textarea/> and <plaintext/>.
            if (!IsModeControlledExternally)
            {
                var name = CurrentTagName();
                if (name.TryGetCompactKey(out var key))
                {
                    switch (key)
                    {
                        case TitleKey:
                            _rawEndTag = "rcdata:title";
                            break;
                        case TextareaKey:
                            _rawEndTag = "rcdata:textarea";
                            break;
                        case StyleKey:
                            _rawEndTag = "style";
                            break;
                        case XmpKey:
                            _rawEndTag = "xmp";
                            break;
                        case IframeKey:
                            _rawEndTag = "iframe";
                            break;
                        case NoEmbedKey:
                            _rawEndTag = "noembed";
                            break;
                        case NoFramesKey:
                            _rawEndTag = "noframes";
                            break;
                        case ScriptKey:
                            _rawEndTag = "script";
                            _state = State.ScriptData;
                            break;
                        case PlaintextKey:
                            _state = State.Plaintext;
                            break;
                    }
                }
            }
        }
        Clear(_name);
        _isEndTag = false;
        _startTagEmitted = false;
        if (_state is not State.Plaintext and not State.ScriptData)
        {
            _state = _rawEndTag is null ? State.Data : State.RawText;
        }
    }
}
