using System.Text;
using AngleSharp.ReadOnlyDom.Streaming.Utf8Tokenizer;

namespace AngleSharp.ReadOnlyDom.Streaming.Internal;

internal static class HtmlEncodingSniffer
{
    internal const int PrefixSize = 1024;
    private static readonly ulong CharsetKey = CompactKey("charset"u8);
    private static readonly ulong ContentKey = CompactKey("content"u8);
    private static readonly ulong MetaKey = CompactKey("meta"u8);

    static HtmlEncodingSniffer()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static ulong CompactKey(ReadOnlySpan<byte> name) =>
        Utf8HtmlName.TryGetCompactKey(name, out var key)
            ? key
            : throw new InvalidOperationException("The encoding-sniffer name has no compact identity.");

    internal static Detection Detect(ReadOnlySpan<byte> source, Encoding? fallback)
    {
        if (source.Length >= 3 && source[0] == 0xef && source[1] == 0xbb && source[2] == 0xbf)
            return new Detection(Encoding.UTF8, 3);
        if (source.Length >= 2 && source[0] == 0xff && source[1] == 0xfe)
            return new Detection(Encoding.Unicode, 2);
        if (source.Length >= 2 && source[0] == 0xfe && source[1] == 0xff)
            return new Detection(Encoding.BigEndianUnicode, 2);

        var sink = new EncodingDeclarationSink();
        var tokenizer = new Utf8HtmlTokenizer(sink);
        var input = new Utf8HtmlTokenizerInput(tokenizer);
        input.Write(source);
        input.Complete();

        return new Detection(sink.DetectedEncoding ?? fallback ?? Encoding.GetEncoding(1252), 0);
    }

    private sealed class EncodingDeclarationSink : IUtf8HtmlTokenSink
    {
        private enum AttributeKind : byte
        {
            None,
            Charset,
            Content,
            HttpEquiv,
        }

        public Utf8HtmlTokenCapture Capture => Utf8HtmlTokenCapture.None;

        private bool _isMeta;
        private string? _charset;
        private string? _content;
        private string? _httpEquiv;
        private AttributeKind _pendingAttribute;

        internal Encoding? DetectedEncoding { get; private set; }

        public void Text(ReadOnlySpan<byte> utf8) { }

        public Utf8HtmlStartTagCapture StartTag(Utf8HtmlName name)
        {
            _isMeta = name.TryGetCompactKey(out var key) && key == MetaKey;
            _charset = null;
            _content = null;
            _httpEquiv = null;
            _pendingAttribute = AttributeKind.None;
            return _isMeta ? Utf8HtmlStartTagCapture.Attributes : Utf8HtmlStartTagCapture.None;
        }

        public bool WantsAttribute(Utf8HtmlName name)
        {
            _pendingAttribute = AttributeKind.None;
            if (!_isMeta)
                return false;

            if (name.TryGetCompactKey(out var key))
            {
                _pendingAttribute =
                    key == CharsetKey ? AttributeKind.Charset
                    : key == ContentKey ? AttributeKind.Content
                    : AttributeKind.None;
            }
            else if (name.SemanticEquals("http-equiv"u8))
            {
                _pendingAttribute = AttributeKind.HttpEquiv;
            }

            return _pendingAttribute != AttributeKind.None;
        }

        public void Attribute(Utf8HtmlName name, ReadOnlySpan<byte> value)
        {
            if (!_isMeta)
                return;

            switch (_pendingAttribute)
            {
                case AttributeKind.Charset when _charset is null:
                    _charset = Encoding.ASCII.GetString(value).Trim();
                    break;
                case AttributeKind.Content when _content is null:
                    _content = Encoding.ASCII.GetString(value);
                    break;
                case AttributeKind.HttpEquiv when _httpEquiv is null:
                    _httpEquiv = Encoding.ASCII.GetString(value).Trim();
                    break;
            }
            _pendingAttribute = AttributeKind.None;
        }

        public void StartTagEnd(bool selfClosing)
        {
            if (!_isMeta || DetectedEncoding is not null)
                return;

            if (TryResolve(_charset, out var direct))
            {
                DetectedEncoding = NormalizeMetaEncoding(direct);
                return;
            }

            if (
                _httpEquiv?.Equals("content-type", StringComparison.OrdinalIgnoreCase) == true
                && _content is not null
                && HtmlEncodingLabels.TryParseContentType(_content, out var contentEncoding)
            )
            {
                DetectedEncoding = NormalizeMetaEncoding(contentEncoding);
            }
        }

        public void EndTag(Utf8HtmlName name) { }

        private static bool TryResolve(string? label, out Encoding encoding)
        {
            if (HtmlEncodingLabels.TryResolve(label, out encoding))
                return true;

            encoding = Encoding.UTF8;
            return false;
        }

        private static Encoding? NormalizeMetaEncoding(Encoding? encoding) =>
            encoding?.CodePage is 1200 or 1201 ? Encoding.UTF8 : encoding;
    }

    internal readonly record struct Detection(Encoding Encoding, int PreambleLength);
}
