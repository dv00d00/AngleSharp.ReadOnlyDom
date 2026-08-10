namespace AngleSharp.ReadOnlyDom.Streaming.Tokenization;

internal partial class Utf8HtmlTokenizerCore
{
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

    private void ProcessDoctypeState(Byte value, ref Boolean reconsume)
    {
        switch (_state)
        {
            case State.Doctype:
                if (value == (Byte)'>')
                {
                    EmitDoctype(forceEofQuirks: false);
                    _state = State.Data;
                }
                else
                {
                    Append(_candidate, value);
                }

                break;
            default:
                throw new InvalidOperationException($"Unexpected {nameof(State)} value: {_state}");
        }
    }

    private void EmitDoctype(Boolean forceEofQuirks)
    {
        var source = _candidate.WrittenSpan;
        var index = 0;
        var quirks = false;
        var publicMissing = true;
        var systemMissing = true;
        var state = DoctypeState.BeforeName;
        Clear(_name);
        Clear(_doctypePublic);
        Clear(_doctypeSystem);

        while (index < source.Length)
        {
            var value = source[index++];
            switch (state)
            {
                case DoctypeState.BeforeName:
                    if (IsSpace(value))
                    {
                        break;
                    }

                    AppendReplacedNull(_name, value, lowerAscii: true);
                    state = DoctypeState.Name;
                    break;
                case DoctypeState.Name:
                    if (IsSpace(value))
                    {
                        state = DoctypeState.AfterName;
                    }
                    else
                    {
                        AppendReplacedNull(_name, value, lowerAscii: true);
                    }

                    break;
                case DoctypeState.AfterName:
                    if (IsSpace(value))
                    {
                        break;
                    }

                    index--;
                    if (ConsumeKeyword(source, ref index, "public"u8))
                    {
                        state = DoctypeState.AfterPublicKeyword;
                    }
                    else if (ConsumeKeyword(source, ref index, "system"u8))
                    {
                        state = DoctypeState.AfterSystemKeyword;
                    }
                    else
                    {
                        quirks = true;
                        state = DoctypeState.Bogus;
                    }
                    break;
                case DoctypeState.AfterPublicKeyword:
                    if (IsSpace(value))
                    {
                        state = DoctypeState.BeforePublicIdentifier;
                    }
                    else if (value is (Byte)'"' or (Byte)'\'')
                    {
                        publicMissing = false;
                        state =
                            value == (Byte)'"'
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
                    {
                        break;
                    }

                    if (value is (Byte)'"' or (Byte)'\'')
                    {
                        publicMissing = false;
                        state =
                            value == (Byte)'"'
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
                    var publicQuote = state == DoctypeState.PublicIdentifierDoubleQuoted ? (Byte)'"' : (Byte)'\'';
                    if (value == publicQuote)
                    {
                        state = DoctypeState.AfterPublicIdentifier;
                    }
                    else
                    {
                        AppendReplacedNull(DoctypePublic, value, lowerAscii: false);
                    }

                    break;
                case DoctypeState.AfterPublicIdentifier:
                    if (IsSpace(value))
                    {
                        state = DoctypeState.BetweenPublicAndSystemIdentifiers;
                    }
                    else if (value is (Byte)'"' or (Byte)'\'')
                    {
                        systemMissing = false;
                        state =
                            value == (Byte)'"'
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
                    {
                        break;
                    }

                    if (value is (Byte)'"' or (Byte)'\'')
                    {
                        systemMissing = false;
                        state =
                            value == (Byte)'"'
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
                    {
                        state = DoctypeState.BeforeSystemIdentifier;
                    }
                    else if (value is (Byte)'"' or (Byte)'\'')
                    {
                        systemMissing = false;
                        state =
                            value == (Byte)'"'
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
                    {
                        break;
                    }

                    if (value is (Byte)'"' or (Byte)'\'')
                    {
                        systemMissing = false;
                        state =
                            value == (Byte)'"'
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
                    var systemQuote = state == DoctypeState.SystemIdentifierDoubleQuoted ? (Byte)'"' : (Byte)'\'';
                    if (value == systemQuote)
                    {
                        state = DoctypeState.AfterSystemIdentifier;
                    }
                    else
                    {
                        AppendReplacedNull(DoctypeSystem, value, lowerAscii: false);
                    }

                    break;
                case DoctypeState.AfterSystemIdentifier:
                    if (!IsSpace(value))
                    {
                        state = DoctypeState.Bogus;
                    }

                    break;
                case DoctypeState.Bogus:
                    break;
                default:
                    throw new InvalidOperationException($"Unknown DOCTYPE state: {state}");
            }
        }

        if (_name.WrittenCount == 0)
        {
            quirks = true;
        }

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
        {
            quirks = true;
        }

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
        {
            quirks = true;
        }

        var token = new Utf8DoctypeToken(
            _name.WrittenSpan,
            WrittenSpan(_doctypePublic),
            publicMissing,
            WrittenSpan(_doctypeSystem),
            systemMissing,
            quirks
        );
        _sink.Doctype(in token);
        Clear(_candidate);
        Clear(_name);
        Clear(_doctypePublic);
        Clear(_doctypeSystem);
    }

    private void AppendReplacedNull(Utf8TokenBuffer destination, Byte value, Boolean lowerAscii)
    {
        if (value == 0)
        {
            AppendReplacement(destination);
        }
        else
        {
            Append(destination, lowerAscii ? AsciiLower(value) : value);
        }
    }

    private static Boolean ConsumeKeyword(ReadOnlySpan<Byte> source, ref Int32 index, ReadOnlySpan<Byte> keyword)
    {
        if (
            source.Length - index < keyword.Length
            || !StartsWithAsciiIgnoreCase(source.Slice(index, keyword.Length), keyword)
        )
        {
            return false;
        }

        index += keyword.Length;
        return true;
    }

    private void AppendReplacement(Utf8TokenBuffer destination) => Append(destination, "\uFFFD"u8);
}
