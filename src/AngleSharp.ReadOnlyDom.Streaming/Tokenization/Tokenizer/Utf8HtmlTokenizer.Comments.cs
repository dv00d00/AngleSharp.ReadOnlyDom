using System.Buffers;

namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

internal partial class Utf8HtmlTokenizer<TResourceLimits>
    where TResourceLimits : struct, IResourceLimitPolicy
{
    private static readonly SearchValues<Byte> CommentTerminators = SearchValues.Create("<-\0\r"u8);
    private static readonly SearchValues<Byte> CommentArbitraryAllowed = CreateArbitraryAllowed("<-\0\r"u8);

    private void ProcessMarkupState(Byte value, ref Boolean reconsume)
    {
        switch (_state)
        {
            case State.MarkupDeclaration:
                ProcessMarkupDeclaration(value);
                break;
            case State.CommentStart:
                if (value == (Byte)'-')
                {
                    _state = State.CommentStartDash;
                }
                else if (value == (Byte)'>')
                {
                    EmitComment();
                }
                else
                {
                    Reconsume(ref reconsume, State.Comment);
                }

                break;
            case State.CommentStartDash:
                if (value == (Byte)'-')
                {
                    _state = State.CommentEnd;
                }
                else if (value == (Byte)'>')
                {
                    EmitComment();
                }
                else
                {
                    AppendComment((Byte)'-');
                    Reconsume(ref reconsume, State.Comment);
                }
                break;
            case State.Comment:
                if (value == (Byte)'<')
                {
                    AppendComment(value);
                    _state = State.CommentLessThan;
                }
                else if (value == (Byte)'-')
                {
                    _state = State.CommentEndDash;
                }
                else if (value == 0)
                {
                    AppendCommentReplacement();
                }
                else
                {
                    AppendComment(value);
                }

                break;
            case State.CommentLessThan:
                if (value == (Byte)'!')
                {
                    AppendComment(value);
                    _state = State.CommentLessThanBang;
                }
                else if (value == (Byte)'<')
                {
                    AppendComment(value);
                }
                else
                {
                    Reconsume(ref reconsume, State.Comment);
                }

                break;
            case State.CommentLessThanBang:
                if (value == (Byte)'-')
                {
                    _state = State.CommentLessThanBangDash;
                }
                else
                {
                    Reconsume(ref reconsume, State.Comment);
                }

                break;
            case State.CommentLessThanBangDash:
                if (value == (Byte)'-')
                {
                    _state = State.CommentLessThanBangDashDash;
                }
                else
                {
                    Reconsume(ref reconsume, State.CommentEndDash);
                }

                break;
            case State.CommentLessThanBangDashDash:
                Reconsume(ref reconsume, State.CommentEnd);
                break;
            case State.CommentEndDash:
                if (value == (Byte)'-')
                {
                    _state = State.CommentEnd;
                }
                else
                {
                    AppendComment((Byte)'-');
                    Reconsume(ref reconsume, State.Comment);
                }
                break;
            case State.CommentEnd:
                if (value == (Byte)'>')
                {
                    EmitComment();
                }
                else if (value == (Byte)'!')
                {
                    _state = State.CommentEndBang;
                }
                else if (value == (Byte)'-')
                {
                    AppendComment(value);
                }
                else
                {
                    AppendComment("--"u8);
                    Reconsume(ref reconsume, State.Comment);
                }
                break;
            case State.CommentEndBang:
                if (value == (Byte)'>')
                {
                    EmitComment();
                }
                else
                {
                    AppendComment("--!"u8);
                    if (value == (Byte)'-')
                    {
                        _state = State.CommentEndDash;
                    }
                    else
                    {
                        Reconsume(ref reconsume, State.Comment);
                    }
                }
                break;
            case State.BogusComment:
                if (value == (Byte)'>')
                {
                    EmitComment();
                }
                else
                {
                    AppendCommentReplacedNull(value);
                }

                break;
            case State.ProcessingInstruction:
                if (value == (Byte)'>')
                {
                    EmitProcessingInstruction();
                }
                else if (!SkipProcessingInstructions)
                {
                    AppendReplacedNull(_candidate, value, lowerAscii: false);
                }
                break;
            default:
                throw new InvalidOperationException($"Unexpected {nameof(State)} value: {_state}");
        }
    }

    private void ProcessMarkupDeclaration(Byte value)
    {
        if (value == (Byte)'>')
        {
            EmitComment();
            return;
        }

        AppendReplacedNull(_candidate, value, lowerAscii: false);
        var candidate = _candidate.WrittenSpan;
        if ("--"u8.StartsWith(candidate))
        {
            if (candidate.Length == 2)
            {
                Clear(_candidate);
                _state = State.CommentStart;
            }
            return;
        }
        if (StartsWithAsciiIgnoreCase("doctype"u8, candidate))
        {
            if (candidate.Length == 7)
            {
                Clear(_candidate);
                _state = State.Doctype;
            }
            return;
        }
        if (IsAcceptingCharacterData && "[CDATA["u8.StartsWith(candidate))
        {
            if (candidate.Length == 7)
            {
                Clear(_candidate);
                _state = State.CDataSection;
            }
            return;
        }
        _state = State.BogusComment;
    }

    private void EmitComment()
    {
        if (_streamingCommentSink is null)
        {
            _sink.Comment(_candidate.WrittenSpan);
        }
        else
        {
            EnsureStreamingCommentStarted();
            _streamingCommentSink.EndComment();
            _streamingCommentStarted = false;
            _captureStreamingComment = false;
        }
        Clear(_candidate);
        _state = State.Data;
    }

    private void EmitProcessingInstruction()
    {
        _sink.ProcessingInstruction(_candidate.WrittenSpan);
        Clear(_candidate);
        _state = State.Data;
    }

    private void AppendComment(Byte value)
    {
        Span<Byte> bytes = stackalloc Byte[1];
        bytes[0] = value;
        AppendComment(bytes);
    }

    private void AppendComment(ReadOnlySpan<Byte> value)
    {
        if (_streamingCommentSink is null)
        {
            Append(_candidate, value);
            return;
        }

        EnsureStreamingCommentStarted();
        if (_captureStreamingComment)
        {
            _streamingCommentSink.CommentChunk(value);
        }
    }

    private void AppendCommentReplacement() => AppendComment("\uFFFD"u8);

    private void AppendCommentReplacedNull(Byte value) =>
        AppendComment(value == 0 ? "\uFFFD"u8 : new ReadOnlySpan<Byte>(in value));

    private void EnsureStreamingCommentStarted()
    {
        if (_streamingCommentStarted)
        {
            return;
        }

        _captureStreamingComment = _streamingCommentSink!.BeginComment();
        _streamingCommentStarted = true;
        if (_captureStreamingComment && _candidate.WrittenCount != 0)
        {
            _streamingCommentSink.CommentChunk(_candidate.WrittenSpan);
        }
        Clear(_candidate);
    }
}
