using System.Text;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Streaming;

internal static class HtmlEncodingSniffer
{
    internal const int PrefixSize = 1024;

    static HtmlEncodingSniffer()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

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
        public Utf8HtmlTokenCapture Capture => Utf8HtmlTokenCapture.None;

        private bool _isMeta;
        private string? _charset;
        private string? _content;
        private string? _httpEquiv;

        internal Encoding? DetectedEncoding { get; private set; }

        public void Text(ReadOnlySpan<byte> utf8) { }

        public Utf8HtmlStartTagCapture StartTag(Utf8HtmlName name)
        {
            _isMeta = name.SemanticEquals("meta"u8);
            _charset = null;
            _content = null;
            _httpEquiv = null;
            return _isMeta
                ? Utf8HtmlStartTagCapture.Attributes
                : Utf8HtmlStartTagCapture.None;
        }

        public bool WantsAttribute(Utf8HtmlName name) =>
            _isMeta
            && (
                name.SemanticEquals("charset"u8)
                || name.SemanticEquals("content"u8)
                || name.SemanticEquals("http-equiv"u8)
            );

        public void Attribute(Utf8HtmlName name, ReadOnlySpan<byte> value)
        {
            if (!_isMeta)
                return;

            if (_charset is null && name.SemanticEquals("charset"u8))
                _charset = Encoding.ASCII.GetString(value).Trim();
            else if (_content is null && name.SemanticEquals("content"u8))
                _content = Encoding.ASCII.GetString(value);
            else if (_httpEquiv is null && name.SemanticEquals("http-equiv"u8))
                _httpEquiv = Encoding.ASCII.GetString(value).Trim();
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

            if (_httpEquiv?.Equals("content-type", StringComparison.OrdinalIgnoreCase) == true && _content is not null)
            {
                DetectedEncoding = NormalizeMetaEncoding(TextEncoding.Parse(_content));
            }
        }

        public void EndTag(Utf8HtmlName name) { }

        private static bool TryResolve(string? label, out Encoding encoding)
        {
            if (label is not null && TextEncoding.IsSupported(label))
            {
                encoding = TextEncoding.Resolve(label);
                return true;
            }

            encoding = Encoding.UTF8;
            return false;
        }

        private static Encoding? NormalizeMetaEncoding(Encoding? encoding) =>
            encoding?.CodePage is 1200 or 1201 ? Encoding.UTF8 : encoding;
    }

    internal readonly record struct Detection(Encoding Encoding, int PreambleLength);
}
