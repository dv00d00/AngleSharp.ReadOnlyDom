#if NET10_0
using System.Collections.Concurrent;
using AngleSharp.Html.Dom;
using AngleSharp.ReadOnlyDom.CompactPrototype;

namespace AngleSharp.Readonly.Tests;

public class TopLevelArenaSmoke
{
    private const string BaseDir = @".\temp\";

    private static readonly ConcurrentDictionary<string, string> FileContents = new();
    private static readonly ConcurrentDictionary<string, IHtmlDocument> ParsedMutableDocs = new();
    private static readonly ConcurrentDictionary<string, CompactDocument> ParsedArenaDocs = new();

    private static string GetHtml(string fileName) =>
        FileContents.GetOrAdd(fileName, static fileName => File.ReadAllText(BaseDir + fileName));

    private static (IHtmlDocument Mutable, CompactDocument Arena) GetDocs(string fileName) =>
        (
            ParsedMutableDocs.GetOrAdd(fileName, static fileName => TopLevelSmoke.parser.ParseDocument(GetHtml(fileName))),
            ParsedArenaDocs.GetOrAdd(
                fileName,
                static fileName =>
                    CompactParser.Parse(
                        GetHtml(fileName),
                        CompactMetadataOptions.ParentLinks,
                        layout: CompactDocumentLayout.Packed
                    )
            )
        );

    private static int Count(CompactDocument document, params Func<Node, bool>[] chain)
    {
        var count = 0;
        foreach (var candidate in document.Descendants())
        {
            if (!candidate.IsElement || !chain[^1](candidate))
                continue;

            var ancestor = candidate.Parent;
            var matches = true;
            for (var i = chain.Length - 2; i >= 0; i--)
            {
                while (ancestor.Exists && (!ancestor.IsElement || !chain[i](ancestor)))
                    ancestor = ancestor.Parent;
                if (!ancestor.Exists)
                {
                    matches = false;
                    break;
                }
                ancestor = ancestor.Parent;
            }

            if (matches)
                count++;
        }
        return count;
    }

    private static Func<Node, bool>[] CreateChain(string selector) =>
        selector.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(CreatePredicate).ToArray();

    private static Func<Node, bool> CreatePredicate(string selector)
    {
        var hash = selector.IndexOf('#');
        var dot = selector.IndexOf('.');
        var tagEnd = hash < 0 ? dot : dot < 0 ? hash : Math.Min(hash, dot);
        var tag = tagEnd < 0 ? selector : tagEnd == 0 ? null : selector[..tagEnd];
        var id = hash < 0 ? null : selector[(hash + 1)..(dot > hash ? dot : selector.Length)];
        var className = dot < 0 ? null : selector[(dot + 1)..];

        return node =>
            (tag is null || node.Is(tag))
            && (id is null || node.Attr("id").SequenceEqual(id))
            && (className is null || node.HasClass(className));
    }

    [Test]
    public async Task ParentLinksSupportDescendantMatching()
    {
        using var document = CompactParser.Parse(
            "<main><ul class=navigation><li><span>x</span></li></ul></main>",
            CompactMetadataOptions.ParentLinks
        );

        await Assert
            .That(Count(document, n => n.Is("ul") && n.HasClass("navigation"), n => n.Is("span")))
            .IsEqualTo(1);
    }

    [Test]
    [MethodDataSource<TopLevelSmoke>(nameof(TopLevelSmoke.SingleTag))]
    public async Task SameResultTag(string fileName, string tag)
    {
        var (mutable, arena) = GetDocs(fileName);
        var expected = mutable.QuerySelectorAll(tag).Length;
        await Assert.That(Count(arena, n => n.Is(tag))).IsEqualTo(expected);
    }

    [Test]
    [MethodDataSource<TopLevelSmoke>(nameof(TopLevelSmoke.Classes))]
    public async Task SameResultClass(string fileName, string className)
    {
        var (mutable, arena) = GetDocs(fileName);
        var expected = mutable.QuerySelectorAll($".{className}").Length;
        await Assert.That(Count(arena, n => n.HasClass(className))).IsEqualTo(expected);
    }

    [Test]
    [MethodDataSource<TopLevelSmoke>(nameof(TopLevelSmoke.Ids))]
    public async Task SameResultId(string fileName, string id)
    {
        var (mutable, arena) = GetDocs(fileName);
        var expected = mutable.QuerySelectorAll($"#{id}").Length;
        await Assert.That(Count(arena, n => n.Attr("id").SequenceEqual(id))).IsEqualTo(expected);
    }

    [Test]
    [MethodDataSource<TopLevelSmoke>(nameof(TopLevelSmoke.TwoTags))]
    public async Task SameResultTwoTags(string fileName, string tag1, string tag2)
    {
        var (mutable, arena) = GetDocs(fileName);
        var expected = mutable.QuerySelectorAll($"{tag1} {tag2}").Length;
        await Assert.That(Count(arena, n => n.Is(tag1), n => n.Is(tag2))).IsEqualTo(expected);
    }

    [Test]
    [MethodDataSource<TopLevelSmoke>(nameof(TopLevelSmoke.ThreeTags))]
    public async Task SameResultThreeTags(string fileName, string tag1, string tag2, string tag3)
    {
        var (mutable, arena) = GetDocs(fileName);
        var expected = mutable.QuerySelectorAll($"{tag1} {tag2} {tag3}").Length;
        await Assert.That(Count(arena, n => n.Is(tag1), n => n.Is(tag2), n => n.Is(tag3))).IsEqualTo(expected);
    }

    [Test]
    [MethodDataSource<TopLevelSmoke>(nameof(TopLevelSmoke.AllComplexSelectors))]
    public async Task SameResultComplex(TopLevelSmoke.SelectorTestCase testCase)
    {
        var (mutable, arena) = GetDocs(testCase.FileName);
        var expected = mutable.QuerySelectorAll(testCase.CssSelector).Length;
        await Assert.That(Count(arena, CreateChain(testCase.CssSelector))).IsEqualTo(expected);
    }
}
#endif
