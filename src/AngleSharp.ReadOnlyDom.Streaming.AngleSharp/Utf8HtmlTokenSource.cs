using System.Text;
using AngleSharp.Html.Dom.Events;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Streaming.AngleSharp;

/// <summary>
/// Adapts segmented UTF-8 input from the RODOM tokenizer to AngleSharp's tree constructor.
/// </summary>
public sealed class Utf8HtmlTokenSource : IHtmlTokenSource, IUtf8HtmlTokenSink
{
    private readonly IAsyncEnumerator<ReadOnlyMemory<byte>> _input;
    private readonly Utf8HtmlTokenizer _tokenizer;
    private readonly Queue<StructHtmlToken> _tokens = new();
    private ReadOnlyMemory<byte> _segment;
    private StructHtmlToken _startTag;
    private int _segmentOffset;
    private bool _inputCompleted;
    private bool _disposed;
    private bool _isAcceptingCharacterData;
    private HtmlParseMode _state;
    private string? _lastStartTagName;
    private ShouldEmitAttribute _shouldEmitAttribute = static (ref StructHtmlToken _, ReadOnlyMemory<char> _) => true;

    public Utf8HtmlTokenSource(
        IAsyncEnumerable<ReadOnlyMemory<byte>> input,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(input);
        _input = input.GetAsyncEnumerator(cancellationToken);
        _tokenizer = new Utf8HtmlTokenizer(this) { IsModeControlledExternally = true };
        State = HtmlParseMode.PCData;
    }

    public event EventHandler<HtmlErrorEvent>? Error;

    public HtmlParseMode State
    {
        get => _state;
        set
        {
            _state = value;
            _tokenizer.SetMode(value, _lastStartTagName);
        }
    }

    public bool IsAcceptingCharacterData
    {
        get => _isAcceptingCharacterData;
        set
        {
            _isAcceptingCharacterData = value;
            _tokenizer.IsAcceptingCharacterData = value;
        }
    }

    public bool IsStrictMode { get; set; }
    public bool IsSupportingProcessingInstructions { get; set; }
    public bool IsNotConsumingCharacterReferences { get; set; }
    public bool IsPreservingAttributeNames { get; set; }
    public bool SkipRawText { get; set; }
    public bool SkipScriptText { get; set; }
    public bool SkipDataText { get; set; }
    public bool SkipComments { get; set; }
    public bool SkipPlaintext { get; set; }
    public bool SkipRCDataText { get; set; }
    public bool SkipCDATA { get; set; }
    public bool SkipProcessingInstructions { get; set; }
    public bool DisableElementPositionTracking { get; set; }

    public ShouldEmitAttribute ShouldEmitAttribute
    {
        get => _shouldEmitAttribute;
        set
        {
            if (value is not null)
                _shouldEmitAttribute = value;
        }
    }

    public Action<HtmlToken, TextRange>? OnToken { get; set; }

    public bool TryGetStructToken(out StructHtmlToken token)
    {
        if (_tokens.TryDequeue(out token))
            return true;

        while (_segmentOffset < _segment.Length)
        {
            _tokenizer.Write(_segment.Span.Slice(_segmentOffset++, 1));
            if (_tokens.TryDequeue(out token))
                return true;
        }

        token = default;
        return false;
    }

    public async Task WaitForInputAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (!_inputCompleted && _segmentOffset >= _segment.Length)
        {
            if (await _input.MoveNextAsync().ConfigureAwait(false))
            {
                _segment = _input.Current;
                _segmentOffset = 0;
                if (!_segment.IsEmpty)
                    return;
            }
            else
            {
                _inputCompleted = true;
                _tokenizer.Complete();
            }
        }
    }

    public void RaiseErrorOccurred(HtmlParseError code, TextPosition position) =>
        Error?.Invoke(this, new HtmlErrorEvent(code, position));

    void IUtf8HtmlTokenSink.Text(ReadOnlySpan<byte> utf8) =>
        _tokens.Enqueue(StructHtmlToken.Character(Encoding.UTF8.GetString(utf8), default));

    void IUtf8HtmlTokenSink.StartTag(ReadOnlySpan<byte> name) =>
        _startTag = StructHtmlToken.Open(_lastStartTagName = Encoding.UTF8.GetString(name));

    void IUtf8HtmlTokenSink.Attribute(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        var attributeName = Encoding.UTF8.GetString(name);
        for (var index = 0; index < _startTag.Attributes.Count; index++)
        {
            if (_startTag.Attributes[index].Name.ToString().Equals(attributeName, StringComparison.OrdinalIgnoreCase))
                return;
        }

        if (ShouldEmitAttribute(ref _startTag, attributeName.AsMemory()))
            _startTag.AddAttribute(attributeName, Encoding.UTF8.GetString(value));
    }

    void IUtf8HtmlTokenSink.StartTagEnd(bool selfClosing)
    {
        _startTag.IsSelfClosing = selfClosing;
        _tokens.Enqueue(_startTag);
    }

    void IUtf8HtmlTokenSink.EndTag(ReadOnlySpan<byte> name) =>
        _tokens.Enqueue(StructHtmlToken.Close(Encoding.UTF8.GetString(name)));

    void IUtf8HtmlTokenSink.Comment(ReadOnlySpan<byte> utf8)
    {
        if (!SkipComments)
            _tokens.Enqueue(StructHtmlToken.Comment(Encoding.UTF8.GetString(utf8), default));
    }

    void IUtf8HtmlTokenSink.Doctype(ReadOnlySpan<byte> utf8) => _tokens.Enqueue(Utf8DoctypeParser.Parse(utf8));

    void IUtf8HtmlTokenSink.EndOfFile() => _tokens.Enqueue(StructHtmlToken.EndOfFile(default));

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _input.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
