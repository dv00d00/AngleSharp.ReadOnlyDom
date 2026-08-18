namespace AngleSharp.ReadOnlyDom.HackerNews.Upstream;

/// <summary>The list pages this sample knows how to read, as an allow list rather than a path template.</summary>
internal static class HackerNewsFeeds
{
    private static readonly Dictionary<string, Uri> Feeds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["news"] = new Uri("https://news.ycombinator.com/news"),
        ["newest"] = new Uri("https://news.ycombinator.com/newest"),
        ["best"] = new Uri("https://news.ycombinator.com/best"),
        ["ask"] = new Uri("https://news.ycombinator.com/ask"),
        ["show"] = new Uri("https://news.ycombinator.com/show"),
        ["jobs"] = new Uri("https://news.ycombinator.com/jobs"),
    };

    internal static bool TryResolve(string? feed, out string name, out Uri uri)
    {
        name = String.IsNullOrWhiteSpace(feed) ? "news" : feed.Trim().ToLowerInvariant();
        return Feeds.TryGetValue(name, out uri!);
    }
}
