using System.Text;

namespace AngleSharp.ReadOnlyDom.Streaming;

/// <summary>Selects how a byte stream's HTML encoding is chosen before query callbacks begin.</summary>
public readonly struct HtmlInputEncoding
{
    private HtmlInputEncoding(Encoding? encoding, Encoding? fallback, bool detect)
    {
        Encoding = encoding;
        Fallback = fallback;
        Detect = detect;
    }

    internal Encoding? Encoding { get; }
    internal Encoding? Fallback { get; }
    internal bool Detect { get; }

    /// <summary>Uses an authoritative encoding without BOM or meta detection.</summary>
    public static HtmlInputEncoding Known(Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        return new HtmlInputEncoding(encoding, fallback: null, detect: false);
    }

    /// <summary>
    /// Detects a UTF-8 or UTF-16 BOM, then an encoding declaration in the first 1024 bytes, and otherwise uses
    /// <paramref name="fallback"/> or Windows-1252.
    /// </summary>
    public static HtmlInputEncoding Auto(Encoding? fallback = null) => new(encoding: null, fallback, detect: true);
}
