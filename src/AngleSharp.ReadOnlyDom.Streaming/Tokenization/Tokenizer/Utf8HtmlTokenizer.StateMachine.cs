using System.Runtime.CompilerServices;

namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

internal partial class Utf8HtmlTokenizerCore
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
        ProcessingInstruction,
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

    private static readonly String[] StateNames = Enum.GetNames<State>();

    private void Process(Byte value)
    {
        var reconsume = true;
        while (reconsume)
        {
            reconsume = false;
            RecordState((Int32)_state, 1);
            switch (_state)
            {
                case State.Data:
                case State.Plaintext:
                    ProcessDataState(value, ref reconsume);
                    break;
                case State.TagOpen:
                case State.EndTagOpen:
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
                    ProcessTagState(value, ref reconsume);
                    break;
                case State.MarkupDeclaration:
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
                case State.ProcessingInstruction:
                    ProcessMarkupState(value, ref reconsume);
                    break;
                case State.Doctype:
                    ProcessDoctypeState(value, ref reconsume);
                    break;
                case State.CharacterReference:
                    ProcessCharacterReference(value, ref reconsume);
                    break;
                case State.CDataSection:
                case State.CDataSectionBracket:
                case State.CDataSectionEnd:
                    ProcessCDataState(value, ref reconsume);
                    break;
                case State.RawText:
                case State.RawLessThan:
                case State.RawEndTagOpen:
                case State.RawEndTagName:
                    ProcessRawTextState(value, ref reconsume);
                    break;
#if DEBUG
                case State.ScriptData:
                case State.ScriptLessThan:
                case State.ScriptEndTagName:
                case State.ScriptEscapeStart:
                case State.ScriptEscapeStartDash:
                case State.ScriptEscaped:
                case State.ScriptEscapedDash:
                case State.ScriptEscapedDashDash:
                case State.ScriptEscapedLessThan:
                case State.ScriptEscapedEndTagName:
                case State.ScriptDoubleEscapeStart:
                case State.ScriptDoubleEscaped:
                case State.ScriptDoubleEscapedDash:
                case State.ScriptDoubleEscapedDashDash:
                case State.ScriptDoubleEscapedLessThan:
                case State.ScriptDoubleEscapeEnd:
                    ProcessScript(value, ref reconsume);
                    break;
                default:
                    ThrowInvalidState(_state);
                    break;
#else
                default:
                    ProcessScript(value, ref reconsume);
                    break;
#endif
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidState(State state)
    {
        throw new ArgumentOutOfRangeException(nameof(state), state, "Invalid tokenizer state; please report a bug.");
    }
}
