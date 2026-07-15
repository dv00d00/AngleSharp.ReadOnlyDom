using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using AngleSharp.Common;
using AngleSharp.Html;
using AngleSharp.Html.Parser;

namespace AngleSharp.ReadOnlyDom.Streaming;

/// <summary>
/// Experimental monotonic UTF-8 tokenizer kernel for read-only construction. HTML syntax is scanned as ASCII bytes;
/// source text is passed directly to the sink and only token parts crossing callbacks use reusable buffers.
/// </summary>
/// <remarks>
/// Token shapes are covered by the pinned html5lib tokenizer corpus across contiguous and segmented UTF-8 input.
/// Parse-error reporting and source positions remain separate conformance work.
/// </remarks>
public sealed class Utf8HtmlTokenizer
{
    private enum State : byte
    {
        Data,
        Plaintext,
        TagOpen,
        EndTagOpen,
        TagName,
        BeforeAttributeName,
        AttributeName,
        AfterAttributeName,
        BeforeAttributeValue,
        AttributeValueDoubleQuoted,
        AttributeValueSingleQuoted,
        AttributeValueUnquoted,
        AfterAttributeValueQuoted,
        SelfClosingStartTag,
        MarkupDeclaration,
        CommentStart,
        CommentStartDash,
        Comment,
        CommentLessThan,
        CommentLessThanBang,
        CommentLessThanBangDash,
        CommentLessThanBangDashDash,
        CommentEndDash,
        CommentEnd,
        CommentEndBang,
        BogusComment,
        Doctype,
        CharacterReference,
        CDataSection,
        CDataSectionBracket,
        CDataSectionEnd,
        RawText,
        RawLessThan,
        RawEndTagOpen,
        RawEndTagName,
        ScriptData,
        ScriptLessThan,
        ScriptEndTagName,
        ScriptEscapeStart,
        ScriptEscapeStartDash,
        ScriptEscaped,
        ScriptEscapedDash,
        ScriptEscapedDashDash,
        ScriptEscapedLessThan,
        ScriptEscapedEndTagName,
        ScriptDoubleEscapeStart,
        ScriptDoubleEscaped,
        ScriptDoubleEscapedDash,
        ScriptDoubleEscapedDashDash,
        ScriptDoubleEscapedLessThan,
        ScriptDoubleEscapeEnd,
    }

    private enum DoctypeState : byte
    {
        BeforeName,
        Name,
        AfterName,
        AfterPublicKeyword,
        BeforePublicIdentifier,
        PublicIdentifierDoubleQuoted,
        PublicIdentifierSingleQuoted,
        AfterPublicIdentifier,
        BetweenPublicAndSystemIdentifiers,
        AfterSystemKeyword,
        BeforeSystemIdentifier,
        SystemIdentifierDoubleQuoted,
        SystemIdentifierSingleQuoted,
        AfterSystemIdentifier,
        Bogus,
    }

    private readonly IUtf8HtmlTokenSink _sink;
    private readonly ArrayBufferWriter<byte> _name = new(32);
    private readonly ArrayBufferWriter<byte> _attributeName = new(32);
    private readonly ArrayBufferWriter<byte> _attributeValue = new(128);
    private readonly ArrayBufferWriter<byte> _seenAttributeNames = new(128);
    private readonly ArrayBufferWriter<byte> _candidate = new(64);
    private readonly ArrayBufferWriter<byte> _doctypePublic = new(64);
    private readonly ArrayBufferWriter<byte> _doctypeSystem = new(64);
    private readonly byte[] _utf8Carry = new byte[4];
    private readonly char[] _entityName = new char[33];
    private State _state;
    private State _returnState;
    private bool _isEndTag;
    private bool _startTagEmitted;
    private bool _pendingCarriageReturn;
    private string? _rawEndTag;
    private long _bytesConsumed;
    private long _segments;
    private long _reconsumes;
    private int _maximumBufferedTokenBytes;
    private int _utf8CarryLength;
    private int _textUtf8CarryLength;
    private int _textUtf8ExpectedLength;
    private uint _textUtf8Carry;
    private bool _numericReferenceOverflow;
    private bool _numericReferenceHasDigits;
    private uint _numericReferenceValue;
    private bool _completed;

    public Utf8HtmlTokenizer(IUtf8HtmlTokenSink sink) => _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    public Utf8HtmlTokenizerCounters Counters =>
        new(_bytesConsumed, _segments, _reconsumes, 0, _maximumBufferedTokenBytes);

    /// <summary>
    /// Applies the tokenizer state selected by an external tree constructor.
    /// </summary>
    public void SetMode(HtmlParseMode mode, string? contextTagName)
    {
        _rawEndTag = mode switch
        {
            HtmlParseMode.RCData => "rcdata:" + (contextTagName ?? "\0"),
            HtmlParseMode.Rawtext => contextTagName ?? "\0",
            HtmlParseMode.Script => contextTagName ?? "script",
            HtmlParseMode.Plaintext => "\0",
            _ => null,
        };
        _state = mode switch
        {
            HtmlParseMode.RCData or HtmlParseMode.Rawtext => State.RawText,
            HtmlParseMode.Script => State.ScriptData,
            HtmlParseMode.Plaintext => State.Plaintext,
            _ => State.Data,
        };
    }

    public bool IsAcceptingCharacterData { get; set; }

    public bool IsModeControlledExternally { get; set; }

    /// <summary>
    /// Enters the CDATA section state after the tree constructor accepts a CDATA declaration in foreign content.
    /// </summary>
    public void EnterCDataSection() => _state = State.CDataSection;

    public void Write(ReadOnlyMemory<byte> utf8)
    {
        ThrowIfCompleted();
        _segments++;
        Write(utf8.Span);
    }

    public void Write(ReadOnlySpan<byte> utf8)
    {
        ThrowIfCompleted();
        _bytesConsumed += utf8.Length;
        var index = 0;
        while (_utf8CarryLength != 0)
        {
            var status = Rune.DecodeFromUtf8(_utf8Carry.AsSpan(0, _utf8CarryLength), out _, out var consumed);
            if (status == OperationStatus.Done)
            {
                WriteValidUtf8(_utf8Carry.AsSpan(0, consumed));
                _utf8CarryLength = 0;
                break;
            }
            if (status == OperationStatus.InvalidData)
            {
                WriteValidUtf8("\uFFFD"u8);
                ShiftUtf8Carry(Math.Max(consumed, 1));
                continue;
            }
            if (index == utf8.Length)
                return;
            _utf8Carry[_utf8CarryLength++] = utf8[index++];
        }

        while (index < utf8.Length)
        {
            var asciiStart = index;
            while (index < utf8.Length && utf8[index] < 0x80)
                index++;
            if (index != asciiStart)
                WriteValidUtf8(utf8[asciiStart..index]);
            if (index == utf8.Length)
                break;

            var status = Rune.DecodeFromUtf8(utf8[index..], out _, out var consumed);
            if (status == OperationStatus.Done)
            {
                WriteValidUtf8(utf8.Slice(index, consumed));
                index += consumed;
            }
            else if (status == OperationStatus.InvalidData)
            {
                WriteValidUtf8("\uFFFD"u8);
                index += Math.Max(consumed, 1);
            }
            else
            {
                utf8[index..].CopyTo(_utf8Carry);
                _utf8CarryLength = utf8.Length - index;
                break;
            }
        }
    }

    private void WriteValidUtf8(ReadOnlySpan<byte> utf8)
    {
        var index = 0;
        while (index < utf8.Length)
        {
            if (
                _state is State.Data or State.RawText or State.Plaintext
                && !_pendingCarriageReturn
                && _textUtf8CarryLength == 0
            )
            {
                var run =
                    _state == State.Plaintext
                        ? FindPlaintextTerminator(utf8[index..])
                        : FindTextTerminator(utf8[index..], _state == State.Data || IsRcData());
                if (run > 0)
                {
                    var text = utf8.Slice(index, run);
                    var safeLength = run == utf8.Length - index ? CompleteUtf8PrefixLength(text) : run;
                    if (safeLength > 0)
                        _sink.Text(text[..safeLength]);
                    if (safeLength != run)
                    {
                        text[safeLength..].CopyTo(_utf8Carry);
                        _utf8CarryLength = run - safeLength;
                    }
                    index += run;
                    continue;
                }
            }

            var value = utf8[index++];
            if (_pendingCarriageReturn)
            {
                _pendingCarriageReturn = false;
                if (value == (byte)'\n')
                    continue;
            }
            if (value == (byte)'\r')
            {
                _pendingCarriageReturn = true;
                value = (byte)'\n';
            }
            Process(value);
        }
    }

    public void Complete()
    {
        if (_completed)
            return;
        if (_utf8CarryLength != 0)
        {
            EmitReplacementCharacter();
            _utf8CarryLength = 0;
        }
        switch (_state)
        {
            case State.TagOpen:
                _sink.Text("<"u8);
                break;
            case State.EndTagOpen:
                _sink.Text("</"u8);
                break;
            case State.CharacterReference:
                ResolveCharacterReference();
                break;
            case State.CDataSectionBracket:
                _sink.Text("]"u8);
                break;
            case State.CDataSectionEnd:
                _sink.Text("]]"u8);
                break;
            case State.RawLessThan:
            case State.RawEndTagOpen:
            case State.RawEndTagName:
                _sink.Text(_candidate.WrittenSpan);
                break;
            case State.CommentStart:
            case State.CommentStartDash:
            case State.Comment:
            case State.CommentLessThan:
            case State.CommentLessThanBang:
            case State.CommentLessThanBangDash:
            case State.CommentLessThanBangDashDash:
            case State.CommentEndDash:
            case State.CommentEnd:
            case State.CommentEndBang:
            case State.BogusComment:
            case State.MarkupDeclaration:
                _sink.Comment(_candidate.WrittenSpan);
                break;
            case State.Doctype:
                EmitDoctype(forceEofQuirks: true);
                break;
            // EOF in a tag discards the incomplete token.
            case State.TagName:
            case State.BeforeAttributeName:
            case State.AttributeName:
            case State.AfterAttributeName:
            case State.BeforeAttributeValue:
            case State.AttributeValueDoubleQuoted:
            case State.AttributeValueSingleQuoted:
            case State.AttributeValueUnquoted:
            case State.AfterAttributeValueQuoted:
            case State.SelfClosingStartTag:
                break;
        }

        _sink.EndOfFile();
        _completed = true;
    }

    public static async ValueTask<Utf8HtmlTokenizerCounters> TokenizeAsync(
        PipeReader reader,
        IUtf8HtmlTokenSink sink,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        var tokenizer = new Utf8HtmlTokenizer(sink);
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            foreach (var segment in buffer)
                tokenizer.Write(segment);
            reader.AdvanceTo(buffer.End);
            if (result.IsCompleted)
                break;
        }
        tokenizer.Complete();
        return tokenizer.Counters;
    }

    private void Process(byte value)
    {
        var reconsume = true;
        while (reconsume)
        {
            reconsume = false;
            if (IsScriptState(_state))
            {
                ProcessScript(value, ref reconsume);
                continue;
            }
            switch (_state)
            {
                case State.Data:
                    if (value == (byte)'<')
                        _state = State.TagOpen;
                    else if (value == (byte)'&')
                        BeginCharacterReference(State.Data);
                    else if (value == (byte)'\r')
                        BeginCarriageReturn();
                    else if (value == 0)
                        EmitByte(value);
                    else
                        EmitByte(value);
                    break;
                case State.Plaintext:
                    if (value == (byte)'\r')
                        BeginCarriageReturn();
                    else if (value == 0)
                        EmitReplacementCharacter();
                    else
                        EmitByte(value);
                    break;
                case State.TagOpen:
                    if (value == (byte)'/')
                        _state = State.EndTagOpen;
                    else if (value == (byte)'!')
                    {
                        _candidate.Clear();
                        _state = State.MarkupDeclaration;
                    }
                    else if (value == (byte)'?')
                    {
                        _candidate.Clear();
                        Append(_candidate, value);
                        _state = State.BogusComment;
                    }
                    else if (IsAsciiLetter(value))
                        BeginTag(isEndTag: false, value);
                    else
                    {
                        _sink.Text("<"u8);
                        Reconsume(ref reconsume, State.Data);
                    }
                    break;
                case State.EndTagOpen:
                    if (IsAsciiLetter(value))
                        BeginTag(isEndTag: true, value);
                    else if (value == (byte)'>')
                        _state = State.Data;
                    else
                    {
                        _candidate.Clear();
                        Reconsume(ref reconsume, State.BogusComment);
                    }
                    break;
                case State.TagName:
                    if (IsSpace(value))
                    {
                        EmitTagStart();
                        _state = State.BeforeAttributeName;
                    }
                    else if (value == (byte)'/')
                    {
                        EmitTagStart();
                        _state = State.SelfClosingStartTag;
                    }
                    else if (value == (byte)'>')
                        FinishTag(selfClosing: false);
                    else
                        AppendReplacedNull(_name, value, lowerAscii: true);
                    break;
                case State.BeforeAttributeName:
                    if (IsSpace(value))
                        break;
                    if (value == (byte)'/')
                    {
                        _state = State.SelfClosingStartTag;
                        break;
                    }
                    if (value == (byte)'>')
                    {
                        FinishTag(selfClosing: false);
                        break;
                    }
                    _attributeName.Clear();
                    _attributeValue.Clear();
                    AppendReplacedNull(_attributeName, value, lowerAscii: true);
                    _state = State.AttributeName;
                    break;
                case State.AttributeName:
                    if (IsSpace(value))
                        _state = State.AfterAttributeName;
                    else if (value == (byte)'=')
                        _state = State.BeforeAttributeValue;
                    else if (value is (byte)'/' or (byte)'>')
                    {
                        CommitAttribute();
                        Reconsume(ref reconsume, State.BeforeAttributeName);
                    }
                    else
                        AppendReplacedNull(_attributeName, value, lowerAscii: true);
                    break;
                case State.AfterAttributeName:
                    if (IsSpace(value))
                        break;
                    if (value == (byte)'=')
                    {
                        _state = State.BeforeAttributeValue;
                        break;
                    }
                    CommitAttribute();
                    Reconsume(ref reconsume, State.BeforeAttributeName);
                    break;
                case State.BeforeAttributeValue:
                    if (IsSpace(value))
                        break;
                    if (value == (byte)'"')
                        _state = State.AttributeValueDoubleQuoted;
                    else if (value == (byte)'\'')
                        _state = State.AttributeValueSingleQuoted;
                    else if (value == (byte)'>')
                    {
                        CommitAttribute();
                        FinishTag(selfClosing: false);
                    }
                    else
                    {
                        _state = State.AttributeValueUnquoted;
                        Reconsume(ref reconsume, _state);
                    }
                    break;
                case State.AttributeValueDoubleQuoted:
                case State.AttributeValueSingleQuoted:
                    var quote = _state == State.AttributeValueDoubleQuoted ? (byte)'"' : (byte)'\'';
                    if (value == quote)
                        _state = State.AfterAttributeValueQuoted;
                    else if (value == (byte)'&')
                        BeginCharacterReference(_state);
                    else
                        AppendReplacedNull(_attributeValue, value, lowerAscii: false);
                    break;
                case State.AttributeValueUnquoted:
                    if (IsSpace(value))
                    {
                        CommitAttribute();
                        _state = State.BeforeAttributeName;
                    }
                    else if (value == (byte)'&')
                        BeginCharacterReference(_state);
                    else if (value == (byte)'>')
                    {
                        CommitAttribute();
                        FinishTag(selfClosing: false);
                    }
                    else
                        AppendReplacedNull(_attributeValue, value, lowerAscii: false);
                    break;
                case State.AfterAttributeValueQuoted:
                    CommitAttribute();
                    if (IsSpace(value))
                        _state = State.BeforeAttributeName;
                    else if (value == (byte)'/')
                        _state = State.SelfClosingStartTag;
                    else if (value == (byte)'>')
                        FinishTag(selfClosing: false);
                    else
                        Reconsume(ref reconsume, State.BeforeAttributeName);
                    break;
                case State.SelfClosingStartTag:
                    if (value == (byte)'>')
                        FinishTag(selfClosing: true);
                    else
                        Reconsume(ref reconsume, State.BeforeAttributeName);
                    break;
                case State.MarkupDeclaration:
                    ProcessMarkupDeclaration(value);
                    break;
                case State.CommentStart:
                    if (value == (byte)'-')
                        _state = State.CommentStartDash;
                    else if (value == (byte)'>')
                        EmitComment();
                    else
                        Reconsume(ref reconsume, State.Comment);
                    break;
                case State.CommentStartDash:
                    if (value == (byte)'-')
                        _state = State.CommentEnd;
                    else if (value == (byte)'>')
                        EmitComment();
                    else
                    {
                        Append(_candidate, (byte)'-');
                        Reconsume(ref reconsume, State.Comment);
                    }
                    break;
                case State.Comment:
                    if (value == (byte)'<')
                    {
                        Append(_candidate, value);
                        _state = State.CommentLessThan;
                    }
                    else if (value == (byte)'-')
                        _state = State.CommentEndDash;
                    else if (value == 0)
                        AppendReplacement(_candidate);
                    else
                        Append(_candidate, value);
                    break;
                case State.CommentLessThan:
                    if (value == (byte)'!')
                    {
                        Append(_candidate, value);
                        _state = State.CommentLessThanBang;
                    }
                    else if (value == (byte)'<')
                        Append(_candidate, value);
                    else
                        Reconsume(ref reconsume, State.Comment);
                    break;
                case State.CommentLessThanBang:
                    if (value == (byte)'-')
                        _state = State.CommentLessThanBangDash;
                    else
                        Reconsume(ref reconsume, State.Comment);
                    break;
                case State.CommentLessThanBangDash:
                    if (value == (byte)'-')
                        _state = State.CommentLessThanBangDashDash;
                    else
                        Reconsume(ref reconsume, State.CommentEndDash);
                    break;
                case State.CommentLessThanBangDashDash:
                    Reconsume(ref reconsume, State.CommentEnd);
                    break;
                case State.CommentEndDash:
                    if (value == (byte)'-')
                        _state = State.CommentEnd;
                    else
                    {
                        Append(_candidate, (byte)'-');
                        Reconsume(ref reconsume, State.Comment);
                    }
                    break;
                case State.CommentEnd:
                    if (value == (byte)'>')
                        EmitComment();
                    else if (value == (byte)'!')
                        _state = State.CommentEndBang;
                    else if (value == (byte)'-')
                        Append(_candidate, value);
                    else
                    {
                        Append(_candidate, "--"u8);
                        Reconsume(ref reconsume, State.Comment);
                    }
                    break;
                case State.CommentEndBang:
                    if (value == (byte)'>')
                        EmitComment();
                    else
                    {
                        Append(_candidate, "--!"u8);
                        if (value == (byte)'-')
                            _state = State.CommentEndDash;
                        else
                            Reconsume(ref reconsume, State.Comment);
                    }
                    break;
                case State.BogusComment:
                    if (value == (byte)'>')
                    {
                        _sink.Comment(_candidate.WrittenSpan);
                        _candidate.Clear();
                        _state = State.Data;
                    }
                    else
                        AppendReplacedNull(_candidate, value, lowerAscii: false);
                    break;
                case State.Doctype:
                    if (value == (byte)'>')
                    {
                        EmitDoctype(forceEofQuirks: false);
                        _state = State.Data;
                    }
                    else
                        Append(_candidate, value);
                    break;
                case State.CharacterReference:
                    ProcessCharacterReference(value, ref reconsume);
                    break;
                case State.CDataSection:
                    if (value == (byte)']')
                        _state = State.CDataSectionBracket;
                    else
                        EmitByte(value);
                    break;
                case State.CDataSectionBracket:
                    if (value == (byte)']')
                        _state = State.CDataSectionEnd;
                    else
                    {
                        _sink.Text("]"u8);
                        Reconsume(ref reconsume, State.CDataSection);
                    }
                    break;
                case State.CDataSectionEnd:
                    if (value == (byte)']')
                        _sink.Text("]"u8);
                    else if (value == (byte)'>')
                        _state = State.Data;
                    else
                    {
                        _sink.Text("]]"u8);
                        Reconsume(ref reconsume, State.CDataSection);
                    }
                    break;
                case State.RawText:
                    if (value == (byte)'<')
                    {
                        _candidate.Clear();
                        Append(_candidate, value);
                        _state = State.RawLessThan;
                    }
                    else if (value == (byte)'&' && IsRcData())
                        BeginCharacterReference(State.RawText);
                    else if (value == (byte)'\r')
                        BeginCarriageReturn();
                    else if (value == 0)
                        EmitReplacementCharacter();
                    else
                        EmitByte(value);
                    break;
                case State.RawLessThan:
                    if (value == (byte)'/')
                    {
                        Append(_candidate, value);
                        _state = State.RawEndTagOpen;
                    }
                    else
                    {
                        _sink.Text(_candidate.WrittenSpan);
                        _candidate.Clear();
                        Reconsume(ref reconsume, State.RawText);
                    }
                    break;
                case State.RawEndTagOpen:
                case State.RawEndTagName:
                    if (IsAsciiLetter(value))
                    {
                        Append(_candidate, AsciiLower(value));
                        _state = State.RawEndTagName;
                    }
                    else if (_state == State.RawEndTagName && IsTagDelimiter(value) && RawCandidateMatches())
                    {
                        _name.Clear();
                        Append(_name, _candidate.WrittenSpan[2..]);
                        _candidate.Clear();
                        _isEndTag = true;
                        _rawEndTag = null;
                        if (value == (byte)'>')
                            FinishTag(selfClosing: false);
                        else if (value == (byte)'/')
                            _state = State.SelfClosingStartTag;
                        else
                            _state = State.BeforeAttributeName;
                    }
                    else
                    {
                        _sink.Text(_candidate.WrittenSpan);
                        _candidate.Clear();
                        Reconsume(ref reconsume, State.RawText);
                    }
                    break;
            }
        }
    }

    private void ProcessMarkupDeclaration(byte value)
    {
        if (value == (byte)'>')
        {
            _sink.Comment(_candidate.WrittenSpan);
            _candidate.Clear();
            _state = State.Data;
            return;
        }

        AppendReplacedNull(_candidate, value, lowerAscii: false);
        var candidate = _candidate.WrittenSpan;
        if ("--"u8.StartsWith(candidate))
        {
            if (candidate.Length == 2)
            {
                _candidate.Clear();
                _state = State.CommentStart;
            }
            return;
        }
        if (StartsWithAsciiIgnoreCase("doctype"u8, candidate))
        {
            if (candidate.Length == 7)
            {
                _candidate.Clear();
                _state = State.Doctype;
            }
            return;
        }
        if (IsAcceptingCharacterData && "[CDATA["u8.StartsWith(candidate))
        {
            if (candidate.Length == 7)
            {
                _candidate.Clear();
                _state = State.CDataSection;
            }
            return;
        }
        _state = State.BogusComment;
    }

    private void EmitComment()
    {
        _sink.Comment(_candidate.WrittenSpan);
        _candidate.Clear();
        _state = State.Data;
    }

    private void ProcessCharacterReference(byte value, ref bool reconsume)
    {
        var source = _candidate.WrittenSpan;
        if (!source.IsEmpty && source[0] == (byte)'#')
        {
            if (source.Length == 1 && value is (byte)'x' or (byte)'X')
            {
                Append(_candidate, value);
                return;
            }

            if (value == (byte)';')
            {
                if (!_numericReferenceOverflow)
                    Append(_candidate, value);
                ResolveCharacterReference();
                _state = _returnState;
                return;
            }

            var isHex = source.Length > 1 && source[1] is (byte)'x' or (byte)'X';
            var isDigit = isHex
                ? (uint)(value - '0') <= 9 || (uint)(AsciiLower(value) - 'a') <= 5
                : (uint)(value - '0') <= 9;
            if (isDigit)
            {
                var digit = (uint)(value - '0') <= 9 ? (uint)(value - '0') : (uint)(AsciiLower(value) - 'a' + 10);
                var radix = isHex ? 16u : 10u;
                _numericReferenceHasDigits = true;
                if (!_numericReferenceOverflow && _numericReferenceValue <= (0x10FFFFu - digit) / radix)
                    _numericReferenceValue = _numericReferenceValue * radix + digit;
                else
                    _numericReferenceOverflow = true;
                if (_candidate.WrittenCount < 32)
                    Append(_candidate, value);
                return;
            }

            ResolveCharacterReference(value);
            Reconsume(ref reconsume, _returnState);
            return;
        }

        if (value == (byte)';')
        {
            Append(_candidate, value);
            ResolveCharacterReference();
            _state = _returnState;
            return;
        }
        var length = _candidate.WrittenCount;
        if (
            length < 32
            && (
                IsAsciiAlphaNumeric(value)
                || (length == 0 && value == (byte)'#')
                || (length == 1 && _candidate.WrittenSpan[0] == (byte)'#' && value is (byte)'x' or (byte)'X')
            )
        )
        {
            Append(_candidate, value);
            return;
        }
        ResolveCharacterReference(value);
        Reconsume(ref reconsume, _returnState);
    }

    private void ResolveCharacterReference(byte? nextInput = null)
    {
        var source = _candidate.WrittenSpan;
        Span<byte> replacement = stackalloc byte[8];
        var replacementLength = 0;
        if (!source.IsEmpty && source[0] == (byte)'#' && _numericReferenceHasDigits)
        {
            var scalar = (int)_numericReferenceValue;
            if (_numericReferenceOverflow)
            {
                replacementLength = Encoding.UTF8.GetBytes("\uFFFD", replacement);
            }
            else if (HtmlEntityProvider.IsInCharacterTable(scalar))
            {
                replacementLength = Encoding.UTF8.GetBytes(HtmlEntityProvider.GetSymbolFromTable(scalar)!, replacement);
            }
            else if (HtmlEntityProvider.IsInvalidNumber(scalar) || !Rune.TryCreate(scalar, out var rune))
            {
                replacementLength = Encoding.UTF8.GetBytes("\uFFFD", replacement);
            }
            else
            {
                replacementLength = rune.EncodeToUtf8(replacement);
            }
        }
        else if (!source.IsEmpty)
        {
            for (var i = 0; i < source.Length; i++)
                _entityName[i] = (char)source[i];
            for (var length = source.Length; length > 0; length--)
            {
                var key = new StringOrMemory(_entityName.AsMemory(0, length));
                var entity = HtmlEntityProvider.ResolverExtended.GetSymbol(key);
                var missingSemicolon = source[length - 1] != (byte)';';
                if (entity is null)
                    continue;
                if (
                    missingSemicolon
                    && IsAttributeReturnState()
                    && (
                        (length < source.Length && (source[length] == '=' || IsAsciiAlphaNumeric(source[length])))
                        || (
                            length == source.Length
                            && nextInput is byte next
                            && (next == '=' || IsAsciiAlphaNumeric(next))
                        )
                    )
                )
                    break;

                var byteCount = Encoding.UTF8.GetByteCount(entity);
                if (byteCount <= replacement.Length)
                {
                    replacementLength = Encoding.UTF8.GetBytes(entity, replacement);
                    AppendCharacterReferenceResult(replacement[..replacementLength]);
                }
                else
                    AppendCharacterReferenceResult(Encoding.UTF8.GetBytes(entity));
                AppendCharacterReferenceResult(source[length..]);
                _candidate.Clear();
                return;
            }
        }

        if (replacementLength != 0)
            AppendCharacterReferenceResult(replacement[..replacementLength]);
        else
        {
            AppendCharacterReferenceResult("&"u8);
            AppendCharacterReferenceResult(source);
        }
        _candidate.Clear();
    }

    private void EmitCharacterReferenceFallback()
    {
        AppendCharacterReferenceResult("&"u8);
        AppendCharacterReferenceResult(_candidate.WrittenSpan);
        _candidate.Clear();
    }

    private void AppendCharacterReferenceResult(ReadOnlySpan<byte> utf8)
    {
        if (_returnState is State.Data or State.RawText)
            _sink.Text(utf8);
        else
            Append(_attributeValue, utf8);
    }

    private void BeginCharacterReference(State returnState)
    {
        _candidate.Clear();
        _numericReferenceOverflow = false;
        _numericReferenceHasDigits = false;
        _numericReferenceValue = 0;
        _returnState = returnState;
        _state = State.CharacterReference;
    }

    private void BeginTag(bool isEndTag, byte firstByte)
    {
        _isEndTag = isEndTag;
        _startTagEmitted = false;
        _name.Clear();
        _attributeName.Clear();
        _attributeValue.Clear();
        _seenAttributeNames.Clear();
        Append(_name, AsciiLower(firstByte));
        _state = State.TagName;
    }

    private void EmitTagStart()
    {
        if (_startTagEmitted || _isEndTag)
            return;
        _sink.StartTag(_name.WrittenSpan);
        _startTagEmitted = true;
    }

    private void CommitAttribute()
    {
        if (_attributeName.WrittenCount == 0)
            return;
        if (_isEndTag)
        {
            _attributeName.Clear();
            _attributeValue.Clear();
            return;
        }
        EmitTagStart();
        if (!HasSeenAttribute(_attributeName.WrittenSpan))
        {
            _sink.Attribute(_attributeName.WrittenSpan, _attributeValue.WrittenSpan);
            Append(_seenAttributeNames, _attributeName.WrittenSpan);
            Append(_seenAttributeNames, (byte)0);
        }
        _attributeName.Clear();
        _attributeValue.Clear();
    }

    private bool HasSeenAttribute(ReadOnlySpan<byte> name)
    {
        var seen = _seenAttributeNames.WrittenSpan;
        while (!seen.IsEmpty)
        {
            var end = seen.IndexOf((byte)0);
            if (end < 0)
                return false;
            if (seen[..end].SequenceEqual(name))
                return true;
            seen = seen[(end + 1)..];
        }
        return false;
    }

    private void EmitDoctype(bool forceEofQuirks)
    {
        var source = _candidate.WrittenSpan;
        var index = 0;
        var quirks = false;
        var publicMissing = true;
        var systemMissing = true;
        var state = DoctypeState.BeforeName;
        _name.Clear();
        _doctypePublic.Clear();
        _doctypeSystem.Clear();

        while (index < source.Length)
        {
            var value = source[index++];
            switch (state)
            {
                case DoctypeState.BeforeName:
                    if (IsSpace(value))
                        break;
                    AppendReplacedNull(_name, value, lowerAscii: true);
                    state = DoctypeState.Name;
                    break;
                case DoctypeState.Name:
                    if (IsSpace(value))
                        state = DoctypeState.AfterName;
                    else
                        AppendReplacedNull(_name, value, lowerAscii: true);
                    break;
                case DoctypeState.AfterName:
                    if (IsSpace(value))
                        break;
                    index--;
                    if (ConsumeKeyword(source, ref index, "public"u8))
                        state = DoctypeState.AfterPublicKeyword;
                    else if (ConsumeKeyword(source, ref index, "system"u8))
                        state = DoctypeState.AfterSystemKeyword;
                    else
                    {
                        quirks = true;
                        state = DoctypeState.Bogus;
                    }
                    break;
                case DoctypeState.AfterPublicKeyword:
                    if (IsSpace(value))
                        state = DoctypeState.BeforePublicIdentifier;
                    else if (value is (byte)'"' or (byte)'\'')
                    {
                        publicMissing = false;
                        state =
                            value == (byte)'"'
                                ? DoctypeState.PublicIdentifierDoubleQuoted
                                : DoctypeState.PublicIdentifierSingleQuoted;
                    }
                    else
                    {
                        quirks = true;
                        state = DoctypeState.Bogus;
                    }
                    break;
                case DoctypeState.BeforePublicIdentifier:
                    if (IsSpace(value))
                        break;
                    if (value is (byte)'"' or (byte)'\'')
                    {
                        publicMissing = false;
                        state =
                            value == (byte)'"'
                                ? DoctypeState.PublicIdentifierDoubleQuoted
                                : DoctypeState.PublicIdentifierSingleQuoted;
                    }
                    else
                    {
                        quirks = true;
                        state = DoctypeState.Bogus;
                    }
                    break;
                case DoctypeState.PublicIdentifierDoubleQuoted:
                case DoctypeState.PublicIdentifierSingleQuoted:
                    var publicQuote = state == DoctypeState.PublicIdentifierDoubleQuoted ? (byte)'"' : (byte)'\'';
                    if (value == publicQuote)
                        state = DoctypeState.AfterPublicIdentifier;
                    else
                        AppendReplacedNull(_doctypePublic, value, lowerAscii: false);
                    break;
                case DoctypeState.AfterPublicIdentifier:
                    if (IsSpace(value))
                        state = DoctypeState.BetweenPublicAndSystemIdentifiers;
                    else if (value is (byte)'"' or (byte)'\'')
                    {
                        systemMissing = false;
                        state =
                            value == (byte)'"'
                                ? DoctypeState.SystemIdentifierDoubleQuoted
                                : DoctypeState.SystemIdentifierSingleQuoted;
                    }
                    else
                    {
                        quirks = true;
                        state = DoctypeState.Bogus;
                    }
                    break;
                case DoctypeState.BetweenPublicAndSystemIdentifiers:
                    if (IsSpace(value))
                        break;
                    if (value is (byte)'"' or (byte)'\'')
                    {
                        systemMissing = false;
                        state =
                            value == (byte)'"'
                                ? DoctypeState.SystemIdentifierDoubleQuoted
                                : DoctypeState.SystemIdentifierSingleQuoted;
                    }
                    else
                    {
                        quirks = true;
                        state = DoctypeState.Bogus;
                    }
                    break;
                case DoctypeState.AfterSystemKeyword:
                    if (IsSpace(value))
                        state = DoctypeState.BeforeSystemIdentifier;
                    else if (value is (byte)'"' or (byte)'\'')
                    {
                        systemMissing = false;
                        state =
                            value == (byte)'"'
                                ? DoctypeState.SystemIdentifierDoubleQuoted
                                : DoctypeState.SystemIdentifierSingleQuoted;
                    }
                    else
                    {
                        quirks = true;
                        state = DoctypeState.Bogus;
                    }
                    break;
                case DoctypeState.BeforeSystemIdentifier:
                    if (IsSpace(value))
                        break;
                    if (value is (byte)'"' or (byte)'\'')
                    {
                        systemMissing = false;
                        state =
                            value == (byte)'"'
                                ? DoctypeState.SystemIdentifierDoubleQuoted
                                : DoctypeState.SystemIdentifierSingleQuoted;
                    }
                    else
                    {
                        quirks = true;
                        state = DoctypeState.Bogus;
                    }
                    break;
                case DoctypeState.SystemIdentifierDoubleQuoted:
                case DoctypeState.SystemIdentifierSingleQuoted:
                    var systemQuote = state == DoctypeState.SystemIdentifierDoubleQuoted ? (byte)'"' : (byte)'\'';
                    if (value == systemQuote)
                        state = DoctypeState.AfterSystemIdentifier;
                    else
                        AppendReplacedNull(_doctypeSystem, value, lowerAscii: false);
                    break;
                case DoctypeState.AfterSystemIdentifier:
                    if (!IsSpace(value))
                        state = DoctypeState.Bogus;
                    break;
                case DoctypeState.Bogus:
                    break;
                default:
                    throw new InvalidOperationException($"Unknown DOCTYPE state: {state}");
            }
        }

        if (_name.WrittenCount == 0)
            quirks = true;

        if (
            state
            is DoctypeState.AfterPublicKeyword
                or DoctypeState.BeforePublicIdentifier
                or DoctypeState.PublicIdentifierDoubleQuoted
                or DoctypeState.PublicIdentifierSingleQuoted
                or DoctypeState.AfterSystemKeyword
                or DoctypeState.BeforeSystemIdentifier
                or DoctypeState.SystemIdentifierDoubleQuoted
                or DoctypeState.SystemIdentifierSingleQuoted
        )
            quirks = true;

        if (
            forceEofQuirks
            && state
                is DoctypeState.BeforeName
                    or DoctypeState.Name
                    or DoctypeState.AfterName
                    or DoctypeState.AfterPublicKeyword
                    or DoctypeState.BeforePublicIdentifier
                    or DoctypeState.PublicIdentifierDoubleQuoted
                    or DoctypeState.PublicIdentifierSingleQuoted
                    or DoctypeState.AfterPublicIdentifier
                    or DoctypeState.BetweenPublicAndSystemIdentifiers
                    or DoctypeState.AfterSystemKeyword
                    or DoctypeState.BeforeSystemIdentifier
                    or DoctypeState.SystemIdentifierDoubleQuoted
                    or DoctypeState.SystemIdentifierSingleQuoted
                    or DoctypeState.AfterSystemIdentifier
        )
            quirks = true;

        var token = new Utf8DoctypeToken(
            _name.WrittenSpan,
            _doctypePublic.WrittenSpan,
            publicMissing,
            _doctypeSystem.WrittenSpan,
            systemMissing,
            quirks
        );
        _sink.Doctype(in token);
        _candidate.Clear();
        _name.Clear();
        _doctypePublic.Clear();
        _doctypeSystem.Clear();
    }

    private void AppendReplacedNull(ArrayBufferWriter<byte> destination, byte value, bool lowerAscii)
    {
        if (value == 0)
            AppendReplacement(destination);
        else
            Append(destination, lowerAscii ? AsciiLower(value) : value);
    }

    private static bool ConsumeKeyword(ReadOnlySpan<byte> source, ref int index, ReadOnlySpan<byte> keyword)
    {
        if (
            source.Length - index < keyword.Length
            || !StartsWithAsciiIgnoreCase(source.Slice(index, keyword.Length), keyword)
        )
            return false;
        index += keyword.Length;
        return true;
    }

    private void AppendReplacement(ArrayBufferWriter<byte> destination) => Append(destination, "\uFFFD"u8);

    private void ProcessScript(byte value, ref bool reconsume)
    {
        switch (_state)
        {
            case State.ScriptData:
                if (value == '<')
                    _state = State.ScriptLessThan;
                else
                    EmitScriptByte(value);
                break;
            case State.ScriptLessThan:
                if (value == '/')
                    BeginScriptEndTag(State.ScriptEndTagName);
                else if (value == '!')
                {
                    _sink.Text("<!"u8);
                    _state = State.ScriptEscapeStart;
                }
                else
                {
                    _sink.Text("<"u8);
                    Reconsume(ref reconsume, State.ScriptData);
                }
                break;
            case State.ScriptEscapeStart:
                if (value == '-')
                {
                    EmitByte(value);
                    _state = State.ScriptEscapeStartDash;
                }
                else
                    Reconsume(ref reconsume, State.ScriptData);
                break;
            case State.ScriptEscapeStartDash:
                if (value == '-')
                {
                    EmitByte(value);
                    _state = State.ScriptEscapedDashDash;
                }
                else
                    Reconsume(ref reconsume, State.ScriptData);
                break;
            case State.ScriptEscaped:
                if (value == '-')
                {
                    EmitByte(value);
                    _state = State.ScriptEscapedDash;
                }
                else if (value == '<')
                    _state = State.ScriptEscapedLessThan;
                else
                    EmitScriptByte(value);
                break;
            case State.ScriptEscapedDash:
                if (value == '-')
                {
                    EmitByte(value);
                    _state = State.ScriptEscapedDashDash;
                }
                else if (value == '<')
                    _state = State.ScriptEscapedLessThan;
                else
                {
                    EmitScriptByte(value);
                    _state = State.ScriptEscaped;
                }
                break;
            case State.ScriptEscapedDashDash:
                if (value == '-')
                    EmitByte(value);
                else if (value == '<')
                    _state = State.ScriptEscapedLessThan;
                else if (value == '>')
                {
                    EmitByte(value);
                    _state = State.ScriptData;
                }
                else
                {
                    EmitScriptByte(value);
                    _state = State.ScriptEscaped;
                }
                break;
            case State.ScriptEscapedLessThan:
                if (value == '/')
                    BeginScriptEndTag(State.ScriptEscapedEndTagName);
                else if (IsAsciiLetter(value))
                {
                    _sink.Text("<"u8);
                    _candidate.Clear();
                    Append(_candidate, AsciiLower(value));
                    EmitByte(value);
                    _state = State.ScriptDoubleEscapeStart;
                }
                else
                {
                    _sink.Text("<"u8);
                    Reconsume(ref reconsume, State.ScriptEscaped);
                }
                break;
            case State.ScriptEndTagName:
                ProcessScriptEndTag(value, State.ScriptData, ref reconsume);
                break;
            case State.ScriptEscapedEndTagName:
                ProcessScriptEndTag(value, State.ScriptEscaped, ref reconsume);
                break;
            case State.ScriptDoubleEscapeStart:
                if (IsAsciiLetter(value))
                {
                    Append(_candidate, AsciiLower(value));
                    EmitByte(value);
                }
                else if (IsTagDelimiter(value))
                {
                    var script = _candidate.WrittenSpan.SequenceEqual("script"u8);
                    _candidate.Clear();
                    EmitByte(value);
                    _state = script ? State.ScriptDoubleEscaped : State.ScriptEscaped;
                }
                else
                {
                    _candidate.Clear();
                    Reconsume(ref reconsume, State.ScriptEscaped);
                }
                break;
            case State.ScriptDoubleEscaped:
                if (value == '-')
                {
                    EmitByte(value);
                    _state = State.ScriptDoubleEscapedDash;
                }
                else if (value == '<')
                {
                    EmitByte(value);
                    _state = State.ScriptDoubleEscapedLessThan;
                }
                else
                    EmitScriptByte(value);
                break;
            case State.ScriptDoubleEscapedDash:
                if (value == '-')
                {
                    EmitByte(value);
                    _state = State.ScriptDoubleEscapedDashDash;
                }
                else if (value == '<')
                {
                    EmitByte(value);
                    _state = State.ScriptDoubleEscapedLessThan;
                }
                else
                {
                    EmitScriptByte(value);
                    _state = State.ScriptDoubleEscaped;
                }
                break;
            case State.ScriptDoubleEscapedDashDash:
                if (value == '-')
                    EmitByte(value);
                else if (value == '<')
                {
                    EmitByte(value);
                    _state = State.ScriptDoubleEscapedLessThan;
                }
                else if (value == '>')
                {
                    EmitByte(value);
                    _state = State.ScriptData;
                }
                else
                {
                    EmitScriptByte(value);
                    _state = State.ScriptDoubleEscaped;
                }
                break;
            case State.ScriptDoubleEscapedLessThan:
                if (value == '/')
                {
                    EmitByte(value);
                    _candidate.Clear();
                    _state = State.ScriptDoubleEscapeEnd;
                }
                else
                    Reconsume(ref reconsume, State.ScriptDoubleEscaped);
                break;
            case State.ScriptDoubleEscapeEnd:
                if (IsAsciiLetter(value))
                {
                    Append(_candidate, AsciiLower(value));
                    EmitByte(value);
                }
                else if (IsTagDelimiter(value))
                {
                    var script = _candidate.WrittenSpan.SequenceEqual("script"u8);
                    _candidate.Clear();
                    EmitByte(value);
                    _state = script ? State.ScriptEscaped : State.ScriptDoubleEscaped;
                }
                else
                {
                    _candidate.Clear();
                    Reconsume(ref reconsume, State.ScriptDoubleEscaped);
                }
                break;
        }
    }

    private void BeginScriptEndTag(State state)
    {
        _candidate.Clear();
        Append(_candidate, "</"u8);
        _state = state;
    }

    private void ProcessScriptEndTag(byte value, State fallback, ref bool reconsume)
    {
        if (IsAsciiLetter(value))
        {
            Append(_candidate, AsciiLower(value));
            return;
        }
        var candidate = _candidate.WrittenSpan;
        if (RawCandidateMatches() && IsTagDelimiter(value))
        {
            _name.Clear();
            Append(_name, candidate[2..]);
            _candidate.Clear();
            _isEndTag = true;
            _rawEndTag = null;
            if (value == '>')
                FinishTag(false);
            else if (value == '/')
                _state = State.SelfClosingStartTag;
            else
                _state = State.BeforeAttributeName;
            return;
        }
        _sink.Text(_candidate.WrittenSpan);
        _candidate.Clear();
        Reconsume(ref reconsume, fallback);
    }

    private void EmitScriptByte(byte value)
    {
        if (value == 0)
            EmitReplacementCharacter();
        else if (value == '\r')
            BeginCarriageReturn();
        else
            EmitByte(value);
    }

    private static bool IsScriptState(State state) => state is >= State.ScriptData and <= State.ScriptDoubleEscapeEnd;

    private void FinishTag(bool selfClosing)
    {
        CommitAttribute();
        if (_isEndTag)
        {
            _sink.EndTag(_name.WrittenSpan);
            _rawEndTag = null;
        }
        else
        {
            EmitTagStart();
            _sink.StartTagEnd(selfClosing);
            if (!selfClosing && !IsModeControlledExternally)
            {
                var name = _name.WrittenSpan;
                if (name.SequenceEqual("title"u8) || name.SequenceEqual("textarea"u8))
                    _rawEndTag = "rcdata:" + Encoding.ASCII.GetString(name);
                else if (
                    name.SequenceEqual("style"u8)
                    || name.SequenceEqual("xmp"u8)
                    || name.SequenceEqual("iframe"u8)
                    || name.SequenceEqual("noembed"u8)
                    || name.SequenceEqual("noframes"u8)
                )
                    _rawEndTag = Encoding.ASCII.GetString(name);
                else if (name.SequenceEqual("script"u8))
                {
                    _rawEndTag = "script";
                    _state = State.ScriptData;
                }
                else if (name.SequenceEqual("plaintext"u8))
                    _state = State.Plaintext;
            }
        }
        _name.Clear();
        _isEndTag = false;
        _startTagEmitted = false;
        if (_state is not State.Plaintext and not State.ScriptData)
            _state = _rawEndTag is null ? State.Data : State.RawText;
    }

    private bool RawCandidateMatches()
    {
        var expected = RawName();
        if (expected is null || _candidate.WrittenCount != expected.Length + 2)
            return false;
        var candidate = _candidate.WrittenSpan[2..];
        for (var index = 0; index < candidate.Length; index++)
        {
            if (candidate[index] != (byte)expected[index])
                return false;
        }
        return true;
    }

    private string? RawName() =>
        _rawEndTag?.StartsWith("rcdata:", StringComparison.Ordinal) == true ? _rawEndTag[7..] : _rawEndTag;

    private bool IsRcData() => _rawEndTag?.StartsWith("rcdata:", StringComparison.Ordinal) == true;

    private void BeginCarriageReturn()
    {
        EmitNormalizedLineFeed();
        _pendingCarriageReturn = true;
    }

    private void EmitNormalizedLineFeed() => _sink.Text("\n"u8);

    private void EmitReplacementCharacter() => _sink.Text("\uFFFD"u8);

    private void EmitByte(byte value)
    {
        if (_textUtf8CarryLength != 0)
        {
            _textUtf8Carry |= (uint)value << (_textUtf8CarryLength++ * 8);
            if (_textUtf8CarryLength == _textUtf8ExpectedLength)
            {
                Span<byte> scalar = stackalloc byte[4];
                for (var index = 0; index < _textUtf8CarryLength; index++)
                    scalar[index] = (byte)(_textUtf8Carry >> (index * 8));
                _sink.Text(scalar[.._textUtf8CarryLength]);
                _textUtf8Carry = 0;
                _textUtf8CarryLength = 0;
                _textUtf8ExpectedLength = 0;
            }
            return;
        }
        if (value >= 0x80)
        {
            _textUtf8Carry = value;
            _textUtf8CarryLength = 1;
            _textUtf8ExpectedLength = Utf8SequenceLength(value);
            return;
        }
        Span<byte> single = stackalloc byte[1];
        single[0] = value;
        _sink.Text(single);
    }

    private void Reconsume(ref bool reconsume, State state)
    {
        _state = state;
        reconsume = true;
        _reconsumes++;
    }

    private void Append(ArrayBufferWriter<byte> buffer, byte value)
    {
        buffer.GetSpan(1)[0] = value;
        buffer.Advance(1);
        ObserveBuffers();
    }

    private void Append(ArrayBufferWriter<byte> buffer, ReadOnlySpan<byte> value)
    {
        buffer.Write(value);
        ObserveBuffers();
    }

    private void ObserveBuffers()
    {
        _maximumBufferedTokenBytes = Math.Max(
            _maximumBufferedTokenBytes,
            _name.WrittenCount + _attributeName.WrittenCount + _attributeValue.WrittenCount + _candidate.WrittenCount
        );
    }

    private void ShiftUtf8Carry(int consumed)
    {
        _utf8Carry.AsSpan(consumed, _utf8CarryLength - consumed).CopyTo(_utf8Carry);
        _utf8CarryLength -= consumed;
    }

    private static int CompleteUtf8PrefixLength(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
            return 0;
        var lead = value.Length - 1;
        while (lead > 0 && value[lead] is >= 0x80 and <= 0xBF && value.Length - lead < 4)
            lead--;
        var expected = Utf8SequenceLength(value[lead]);
        return expected > 1 && value.Length - lead < expected ? lead : value.Length;
    }

    private static int Utf8SequenceLength(byte lead) =>
        lead switch
        {
            < 0x80 => 1,
            < 0xE0 => 2,
            < 0xF0 => 3,
            < 0xF8 => 4,
            _ => 1,
        };

    private static int FindTextTerminator(ReadOnlySpan<byte> value, bool includeAmpersand)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (
                current == (byte)'<'
                || current == 0
                || current == (byte)'\r'
                || (includeAmpersand && current == (byte)'&')
            )
                return i;
        }
        return value.Length;
    }

    private static int FindPlaintextTerminator(ReadOnlySpan<byte> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] is 0 or (byte)'\r')
                return i;
        }
        return value.Length;
    }

    private bool IsAttributeReturnState() => _returnState is not State.Data and not State.RawText;

    private static bool StartsWithAsciiIgnoreCase(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> candidate)
    {
        if (candidate.Length > expected.Length)
            return false;
        for (var i = 0; i < candidate.Length; i++)
        {
            if (AsciiLower(expected[i]) != AsciiLower(candidate[i]))
                return false;
        }
        return true;
    }

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && IsSpace(value[start]))
            start++;
        while (end > start && IsSpace(value[end - 1]))
            end--;
        return value[start..end];
    }

    private static bool IsSpace(byte value) => value is 0x09 or 0x0A or 0x0C or 0x0D or 0x20;

    private static bool IsAsciiLetter(byte value) =>
        (uint)(value - 'A') <= 'Z' - 'A' || (uint)(value - 'a') <= 'z' - 'a';

    private static bool IsAsciiAlphaNumeric(byte value) => IsAsciiLetter(value) || (uint)(value - '0') <= 9;

    private static bool IsTagDelimiter(byte value) => value is (byte)'>' or (byte)'/' || IsSpace(value);

    private static byte AsciiLower(byte value) => (uint)(value - 'A') <= 'Z' - 'A' ? (byte)(value + 0x20) : value;

    private void ThrowIfCompleted()
    {
        if (_completed)
            throw new InvalidOperationException("The tokenizer is already complete.");
    }
}
