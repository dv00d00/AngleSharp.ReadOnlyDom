using System.Text;
using AngleSharp.ReadOnlyDom.HackerNews.Ndjson;
using AngleSharp.ReadOnlyDom.Streaming.Output;
using AngleSharp.ReadOnlyDom.Streaming.Query;

namespace AngleSharp.ReadOnlyDom.HackerNews.Preview;

/// <summary>
/// Folds a linked page's head into the NDJSON a link-preview card is built from. Card fields carry a
/// weight so the browser can keep the best value seen so far without the server buffering the head to
/// decide — <c>og:title</c> outranks <c>twitter:title</c> outranks <c>&lt;title&gt;</c>, whatever order
/// they appear in. When the head ends the card is final, and the reader is told to stop pulling bytes.
/// </summary>
internal sealed class PreviewBuffer : IUtf8PublishSource, IDisposable
{
    private const int OpenGraphWeight = 3;
    private const int TwitterWeight = 2;
    private const int DocumentWeight = 1;

    /// <summary>The card's fields, in the order their weights are tracked.</summary>
    private enum CardField
    {
        Title,
        Description,
        Image,
        Icon,
        Site,
        Type,
        Canonical,
        Author,
        Published,
        Accent,
    }

    private static readonly byte[][] FieldNames =
    [
        "title"u8.ToArray(),
        "description"u8.ToArray(),
        "image"u8.ToArray(),
        "icon"u8.ToArray(),
        "site"u8.ToArray(),
        "type"u8.ToArray(),
        "canonical"u8.ToArray(),
        "author"u8.ToArray(),
        "published"u8.ToArray(),
        "accent"u8.ToArray(),
    ];

    private readonly NdjsonPublisher _publisher = new(recordTranscript: true);
    private readonly Action? _onCardComplete;
    private readonly int[] _weights = new int[FieldNames.Length];

    private Uri _base;
    private int _fields;
    private bool _completed;

    internal PreviewBuffer(Uri source, Action? onCardComplete = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _base = source;
        _onCardComplete = onCardComplete;

        var json = _publisher.Json;
        json.WriteStartObject();
        json.WriteString("kind"u8, "source"u8);
        json.WriteString("url"u8, source.AbsoluteUri);
        json.WriteString("host"u8, source.Host);
        json.WriteEndObject();
        _publisher.Commit();
    }

    /// <summary>Every NDJSON byte produced by this execution, retained for the preview cache.</summary>
    internal ReadOnlyMemory<byte> Transcript => _publisher.Transcript;

    internal int FieldCount => _fields;

    public ReadOnlyMemory<byte> PublishableUtf8 => _publisher.PublishableUtf8;

    public void AdvancePublished(int bytes) => _publisher.AdvancePublished(bytes);

    public void Dispose() => _publisher.Dispose();

    /// <summary>Applies <c>&lt;base href&gt;</c> so later relative URLs resolve the way a browser would.</summary>
    internal void Base(in CompletedElement element)
    {
        if (
            element.TryGetAttributeUtf8("href"u8, out var href)
            && Uri.TryCreate(_base, Encoding.UTF8.GetString(href), out var resolved)
            && IsWebUrl(resolved)
        )
        {
            _base = resolved;
        }
    }

    internal void DocumentTitle(ReadOnlySpan<byte> text) => WriteMeta(CardField.Title, text, DocumentWeight);

    /// <summary>
    /// Reads one <c>meta</c> element. Both spellings of the key matter: Open Graph uses
    /// <c>property</c>, Twitter and the legacy description use <c>name</c>.
    /// </summary>
    internal void Meta(in CompletedElement element)
    {
        if (!element.TryGetAttributeUtf8("content"u8, out var content) || content.IsEmpty)
            return;
        if (!element.TryGetAttributeUtf8("property"u8, out var key) || key.IsEmpty)
        {
            if (!element.TryGetAttributeUtf8("name"u8, out key) || key.IsEmpty)
                return;
        }

        if (Matches(key, "og:title"u8))
            WriteMeta(CardField.Title, content, OpenGraphWeight);
        else if (Matches(key, "og:description"u8))
            WriteMeta(CardField.Description, content, OpenGraphWeight);
        else if (Matches(key, "og:site_name"u8))
            WriteMeta(CardField.Site, content, OpenGraphWeight);
        else if (Matches(key, "og:type"u8))
            WriteMeta(CardField.Type, content, OpenGraphWeight);
        else if (Matches(key, "og:image"u8) || Matches(key, "og:image:url"u8) || Matches(key, "og:image:secure_url"u8))
            WriteMetaUrl(CardField.Image, content, OpenGraphWeight);
        else if (Matches(key, "og:url"u8))
            WriteMetaUrl(CardField.Canonical, content, OpenGraphWeight);
        else if (Matches(key, "twitter:title"u8))
            WriteMeta(CardField.Title, content, TwitterWeight);
        else if (Matches(key, "twitter:description"u8))
            WriteMeta(CardField.Description, content, TwitterWeight);
        else if (Matches(key, "twitter:site"u8))
            WriteMeta(CardField.Site, content, TwitterWeight);
        else if (Matches(key, "twitter:image"u8) || Matches(key, "twitter:image:src"u8))
            WriteMetaUrl(CardField.Image, content, TwitterWeight);
        else if (Matches(key, "description"u8))
            WriteMeta(CardField.Description, content, DocumentWeight);
        else if (Matches(key, "author"u8))
            WriteMeta(CardField.Author, content, DocumentWeight);
        else if (Matches(key, "article:published_time"u8))
            WriteMeta(CardField.Published, content, TwitterWeight);
        else if (Matches(key, "theme-color"u8))
            WriteMeta(CardField.Accent, content, DocumentWeight);
    }

    /// <summary>Reads the icon and canonical links; <c>rel</c> is a token list, not a single value.</summary>
    internal void Link(in CompletedElement element)
    {
        if (
            !element.TryGetAttributeUtf8("rel"u8, out var rel)
            || rel.IsEmpty
            || !element.TryGetAttributeUtf8("href"u8, out var href)
            || href.IsEmpty
        )
        {
            return;
        }

        if (HasToken(rel, "apple-touch-icon"u8))
            WriteMetaUrl(CardField.Icon, href, TwitterWeight);
        else if (HasToken(rel, "icon"u8))
            WriteMetaUrl(CardField.Icon, href, DocumentWeight);
        else if (HasToken(rel, "canonical"u8))
            WriteMetaUrl(CardField.Canonical, href, TwitterWeight);
        else if (HasToken(rel, "image_src"u8))
            WriteMetaUrl(CardField.Image, href, DocumentWeight);
    }

    /// <summary>
    /// Called when the head ends — everything a card needs has been seen, so nothing that follows is
    /// worth downloading. The rest of the document is usually two orders of magnitude larger than this.
    /// </summary>
    internal void CardComplete()
    {
        if (_completed)
            return;

        _completed = true;
        _onCardComplete?.Invoke();
    }

    internal void Complete()
    {
        if (_fields == 0)
            WriteNote("This page declares no preview metadata."u8);
        CardComplete();
    }

    private void WriteNote(ReadOnlySpan<byte> text)
    {
        var json = _publisher.Json;
        json.WriteStartObject();
        json.WriteString("kind"u8, "note"u8);
        json.WriteString("text"u8, text);
        json.WriteEndObject();
        _publisher.Commit();
    }

    /// <summary>
    /// Publishes a field only when it beats what has already gone out. A head that declares its title
    /// three ways is the norm, and the weaker spellings are not worth a line on the wire.
    /// </summary>
    private void WriteMeta(CardField field, ReadOnlySpan<byte> value, int weight)
    {
        if (value.IsEmpty || !TryClaim(field, weight))
            return;

        var json = _publisher.Json;
        json.WriteStartObject();
        json.WriteString("kind"u8, "meta"u8);
        json.WriteString("field"u8, FieldNames[(int)field]);
        json.WriteString("value"u8, value);
        json.WriteNumber("weight"u8, weight);
        json.WriteEndObject();
        _publisher.Commit();
        _fields++;
    }

    private void WriteMetaUrl(CardField field, ReadOnlySpan<byte> value, int weight)
    {
        // Resolve before claiming, so an unusable URL does not lock the field at this weight.
        if (!TryResolve(value, out var resolved) || !TryClaim(field, weight))
            return;

        var json = _publisher.Json;
        json.WriteStartObject();
        json.WriteString("kind"u8, "meta"u8);
        json.WriteString("field"u8, FieldNames[(int)field]);
        json.WriteString("value"u8, resolved);
        json.WriteNumber("weight"u8, weight);
        json.WriteEndObject();
        _publisher.Commit();
        _fields++;
    }

    private bool TryClaim(CardField field, int weight)
    {
        if (_completed || weight <= _weights[(int)field])
            return false;

        _weights[(int)field] = weight;
        return true;
    }

    private bool TryResolve(ReadOnlySpan<byte> value, out string resolved)
    {
        resolved = String.Empty;
        var text = Encoding.UTF8.GetString(value).Trim();
        if (text.Length == 0 || !Uri.TryCreate(_base, text, out var candidate) || !IsWebUrl(candidate))
            return false;

        resolved = candidate.AbsoluteUri;
        return true;
    }

    private static bool IsWebUrl(Uri value) =>
        value.IsAbsoluteUri && (value.Scheme == Uri.UriSchemeHttp || value.Scheme == Uri.UriSchemeHttps);

    private static bool Matches(ReadOnlySpan<byte> key, ReadOnlySpan<byte> candidate) =>
        Ascii.EqualsIgnoreCase(key, candidate);

    private static bool HasToken(ReadOnlySpan<byte> tokens, ReadOnlySpan<byte> candidate)
    {
        while (!tokens.IsEmpty)
        {
            var end = tokens.IndexOfAny(" \t\n\r\f"u8);
            var token = end < 0 ? tokens : tokens[..end];
            if (Ascii.EqualsIgnoreCase(token, candidate))
                return true;
            if (end < 0)
                return false;
            tokens = tokens[(end + 1)..];
        }

        return false;
    }
}
