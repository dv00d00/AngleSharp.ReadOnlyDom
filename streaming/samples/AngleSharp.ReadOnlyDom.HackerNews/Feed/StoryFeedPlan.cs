using AngleSharp.ReadOnlyDom.Streaming.Query;

namespace AngleSharp.ReadOnlyDom.HackerNews.Feed;

/// <summary>
/// The Hacker News list is a layout table from 2007: a story is one <c>tr.athing</c> row, and everything
/// about it except the title lives in the <em>next</em> row's <c>td.subtext</c>. There is no element that
/// contains a whole story, so the plan opens a record on the title row and closes it when the subtext cell
/// ends. Both rows are just siblings in one lexical pass — nothing is buffered to relate them.
/// </summary>
internal static class StoryFeedPlan
{
    internal static readonly QueryPlan<StoryFeedBuffer> Instance = Create();

    private static QueryPlan<StoryFeedBuffer> Create()
    {
        var html = StreamQuery.For<StoryFeedBuffer>("html").OnEnd(static (ref output) => output.CompleteDocument());

        var story = html.Descendant("tr")
            .Class("athing")
            .OnStart(static (ref output, in element) => output.StartStory(in element), "id");
        story.Descendant("span").Class("rank").OnNormalizedText(static (ref o, in e) => o.Rank(e.TextUtf8));

        var titleLine = story.Descendant("span").Class("titleline");

        // Child, not Descendant: the site anchor sits one level deeper inside span.sitebit, so only the
        // story's own link matches here.
        titleLine.Child("a").OnNormalizedText(static (ref output, in element) => output.Title(in element), "href");
        titleLine.Descendant("span").Class("sitestr").OnNormalizedText(static (ref o, in e) => o.Site(e.TextUtf8));

        var subtext = html.Descendant("td").Class("subtext").OnEnd(static (ref output) => output.EndStory());
        subtext.Descendant("span").Class("score").OnNormalizedText(static (ref o, in e) => o.Points(e.TextUtf8));
        subtext.Descendant("a").Class("hnuser").OnNormalizedText(static (ref o, in e) => o.User(e.TextUtf8));
        subtext.Descendant("span").Class("age").OnStart(static (ref o, in e) => o.Age(in e), "title");
        subtext.Descendant("a").OnNormalizedText(static (ref o, in e) => o.SublineLink(in e));

        return html.Compile();
    }
}
