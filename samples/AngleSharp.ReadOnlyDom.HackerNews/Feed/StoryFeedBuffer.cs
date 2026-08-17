using System.Buffers;
using System.Buffers.Text;
using AngleSharp.ReadOnlyDom.HackerNews.Ndjson;
using AngleSharp.ReadOnlyDom.Streaming.Output;
using AngleSharp.ReadOnlyDom.Streaming.Query;

namespace AngleSharp.ReadOnlyDom.HackerNews.Feed;

/// <summary>
/// Folds Hacker News list markup into NDJSON: one story becomes one line. A line is published only when
/// the story's subtext row closes, so the browser can render every row it has received while the rest of
/// the document is still on the wire.
/// </summary>
internal sealed class StoryFeedBuffer : IUtf8PublishSource, IDisposable
{
    private readonly NdjsonPublisher _publisher = new(recordTranscript: true);

    private readonly ArrayBufferWriter<byte> _title = new(192);
    private readonly ArrayBufferWriter<byte> _url = new(256);
    private readonly ArrayBufferWriter<byte> _site = new(64);
    private readonly ArrayBufferWriter<byte> _user = new(32);

    private long _id;
    private long _createdUnixSeconds;
    private int _rank;
    private int _points;
    private int _comments;
    private bool _pending;

    /// <summary>Every NDJSON byte produced by this execution, retained for the short-lived feed snapshot.</summary>
    internal ReadOnlyMemory<byte> Transcript => _publisher.Transcript;

    internal int StoryCount => _publisher.RecordCount;

    public ReadOnlyMemory<byte> PublishableUtf8 => _publisher.PublishableUtf8;

    public void AdvancePublished(int bytes) => _publisher.AdvancePublished(bytes);

    public void Dispose() => _publisher.Dispose();

    internal void StartStory(in Element element)
    {
        if (_pending)
            EmitStory();

        ResetStory();
        if (element.TryGetAttribute("id"u8, out var id) && Utf8Parser.TryParse(id, out long value, out _))
            _id = value;
        _pending = true;
    }

    /// <summary>Reads the leading digits of a rank cell such as <c>1.</c>.</summary>
    internal void Rank(ReadOnlySpan<byte> text)
    {
        if (Utf8Parser.TryParse(text, out int rank, out _))
            _rank = rank;
    }

    internal void Title(in CompletedElement element)
    {
        NdjsonPublisher.Copy(_title, element.TextUtf8);
        if (element.TryGetAttributeUtf8("href"u8, out var href))
            NdjsonPublisher.Copy(_url, href);
    }

    internal void Site(ReadOnlySpan<byte> text) => NdjsonPublisher.Copy(_site, text);

    /// <summary>Reads the leading digits of a score such as <c>459 points</c>.</summary>
    internal void Points(ReadOnlySpan<byte> text)
    {
        if (Utf8Parser.TryParse(text, out int points, out _))
            _points = points;
    }

    internal void User(ReadOnlySpan<byte> text) => NdjsonPublisher.Copy(_user, text);

    /// <summary>
    /// Reads the submission time from an age tooltip such as
    /// <c>title="2026-08-16T23:45:09 1786923909"</c>. Publishing the instant instead of the rendered
    /// phrase lets the page age its own rows between refreshes.
    /// </summary>
    internal void Age(in Element element)
    {
        if (!element.TryGetAttribute("title"u8, out var title))
            return;

        var separator = title.LastIndexOf((byte)' ');
        if (separator >= 0 && Utf8Parser.TryParse(title[(separator + 1)..], out long seconds, out _))
            _createdUnixSeconds = seconds;
    }

    /// <summary>
    /// Picks the comment count out of the subline's untyped anchors. The hide, user, and age links share
    /// the same shape, so the text decides: <c>215 comments</c> counts, <c>discuss</c> means none yet.
    /// </summary>
    internal void SublineLink(in CompletedElement element)
    {
        var text = element.TextUtf8;
        if (text.EndsWith("comments"u8) || text.EndsWith("comment"u8))
        {
            if (Utf8Parser.TryParse(text, out int comments, out _))
                _comments = comments;
        }
        else if (text.SequenceEqual("discuss"u8))
        {
            _comments = 0;
        }
    }

    internal void EndStory()
    {
        if (_pending)
            EmitStory();
    }

    internal void CompleteDocument()
    {
        if (_pending)
            EmitStory();
    }

    private void EmitStory()
    {
        _pending = false;
        if (_id == 0 && _title.WrittenCount == 0)
            return;

        var json = _publisher.Json;
        json.WriteStartObject();
        json.WriteNumber("id"u8, _id);
        json.WriteNumber("rank"u8, _rank);
        json.WriteString("title"u8, _title.WrittenSpan);
        json.WriteString("url"u8, _url.WrittenSpan);
        json.WriteString("site"u8, _site.WrittenSpan);
        json.WriteNumber("points"u8, _points);
        json.WriteString("user"u8, _user.WrittenSpan);
        json.WriteNumber("comments"u8, _comments);
        json.WriteNumber("createdAt"u8, _createdUnixSeconds);
        json.WriteEndObject();
        _publisher.Commit();
    }

    private void ResetStory()
    {
        _title.ResetWrittenCount();
        _url.ResetWrittenCount();
        _site.ResetWrittenCount();
        _user.ResetWrittenCount();
        _id = 0;
        _createdUnixSeconds = 0;
        _rank = 0;
        _points = 0;
        _comments = 0;
    }
}
