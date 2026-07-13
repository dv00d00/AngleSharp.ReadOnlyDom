#if NET10_0
using System.Collections.Concurrent;
using AngleSharp.Html.Dom;
using AngleSharp.ReadOnlyDom.Compact;

namespace AngleSharp.Readonly.Tests;

public class TopLevelArenaSmoke
{
    private const string BaseDir = @".\temp\";

    private static readonly ConcurrentDictionary<string, string> FileContents = new();
    private static readonly ConcurrentDictionary<string, IHtmlDocument> ParsedMutableDocs = new();
    private static readonly ConcurrentDictionary<(string FileName, CompactDocumentLayout Layout), CompactDocument> ParsedArenaDocs = new();
    private static readonly CompactDocumentLayout[] Layouts =
        [CompactDocumentLayout.Packed, CompactDocumentLayout.FrozenColumns];

    private static string GetHtml(string fileName) =>
        FileContents.GetOrAdd(fileName, static fileName => File.ReadAllText(BaseDir + fileName));

    private static (IHtmlDocument Mutable, CompactDocument Arena) GetDocs(
        string fileName,
        CompactDocumentLayout layout
    ) =>
        (
            ParsedMutableDocs.GetOrAdd(fileName, static fileName => TopLevelSmoke.parser.ParseDocument(GetHtml(fileName))),
            ParsedArenaDocs.GetOrAdd(
                (fileName, layout),
                static key =>
                    CompactParser
                        .CreateParser(
                        CompactMetadataOptions.ParentLinks,
                        layout: key.Layout
                        )
                        .ParseCompactDocument(GetHtml(key.FileName))
            )
        );

    public static IEnumerable<(string FileName, string Tag, CompactDocumentLayout Layout)> Tags() =>
        TopLevelSmoke
            .SingleTag()
            .SelectMany(test => Layouts.Select(layout => (test.FileName, test.Tag, layout)))
            .Distinct();

    public static IEnumerable<(string FileName, string ClassName, CompactDocumentLayout Layout)> Classes() =>
        TopLevelSmoke
            .Classes()
            .SelectMany(test => Layouts.Select(layout => (test.FileName, test.ClassName, layout)))
            .Distinct();

    public static IEnumerable<(string FileName, string Id, CompactDocumentLayout Layout)> Ids() =>
        TopLevelSmoke
            .Ids()
            .SelectMany(test => Layouts.Select(layout => (test.FileName, test.Id, layout)))
            .Distinct();

    public static IEnumerable<(string FileName, string Tag1, string Tag2, CompactDocumentLayout Layout)> TwoTags() =>
        TopLevelSmoke.TwoTags().SelectMany(test =>
            Layouts.Select(layout => (test.FileName, test.Tag1, test.Tag2, layout))
        ).Distinct();

    public static IEnumerable<(
        string FileName,
        string Tag1,
        string Tag2,
        string Tag3,
        CompactDocumentLayout Layout
    )> ThreeTags() =>
        TopLevelSmoke.ThreeTags().SelectMany(test =>
            Layouts.Select(layout => (test.FileName, test.Tag1, test.Tag2, test.Tag3, layout))
        ).Distinct();

    public static IEnumerable<(TopLevelSmoke.SelectorTestCase TestCase, CompactDocumentLayout Layout)> Complex() =>
        TopLevelSmoke
            .AllComplexSelectors()
            .SelectMany(testCase => Layouts.Select(layout => (testCase, layout)))
            .Distinct();

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
    [Arguments(CompactDocumentLayout.Packed)]
    [Arguments(CompactDocumentLayout.FrozenColumns)]
    public async Task ParentLinksSupportDescendantMatching(CompactDocumentLayout layout)
    {
        using var document = CompactParser
            .CreateParser(
            CompactMetadataOptions.ParentLinks,
            layout: layout
            )
            .ParseCompactDocument("<main><ul class=navigation><li><span>x</span></li></ul></main>");

        await Assert
            .That(Count(document, n => n.Is("ul") && n.HasClass("navigation"), n => n.Is("span")))
            .IsEqualTo(1);
    }

    [Test]
    [MethodDataSource(nameof(Tags))]
    public async Task SameResultTag(string fileName, string tag, CompactDocumentLayout layout)
    {
        var (mutable, arena) = GetDocs(fileName, layout);
        var expected = mutable.QuerySelectorAll(tag).Length;
        await Assert.That(Count(arena, n => n.Is(tag))).IsEqualTo(expected);
    }

    [Test]
    [MethodDataSource(nameof(Classes))]
    public async Task SameResultClass(string fileName, string className, CompactDocumentLayout layout)
    {
        var (mutable, arena) = GetDocs(fileName, layout);
        var expected = mutable.QuerySelectorAll($".{className}").Length;
        await Assert.That(Count(arena, n => n.HasClass(className))).IsEqualTo(expected);
    }

    [Test]
    [MethodDataSource(nameof(Ids))]
    public async Task SameResultId(string fileName, string id, CompactDocumentLayout layout)
    {
        var (mutable, arena) = GetDocs(fileName, layout);
        var expected = mutable.QuerySelectorAll($"#{id}").Length;
        await Assert.That(Count(arena, n => n.Attr("id").SequenceEqual(id))).IsEqualTo(expected);
    }

    [Test]
    [MethodDataSource(nameof(TwoTags))]
    public async Task SameResultTwoTags(string fileName, string tag1, string tag2, CompactDocumentLayout layout)
    {
        var (mutable, arena) = GetDocs(fileName, layout);
        var expected = mutable.QuerySelectorAll($"{tag1} {tag2}").Length;
        await Assert.That(Count(arena, n => n.Is(tag1), n => n.Is(tag2))).IsEqualTo(expected);
    }

    [Test]
    [MethodDataSource(nameof(ThreeTags))]
    public async Task SameResultThreeTags(
        string fileName,
        string tag1,
        string tag2,
        string tag3,
        CompactDocumentLayout layout
    )
    {
        var (mutable, arena) = GetDocs(fileName, layout);
        var expected = mutable.QuerySelectorAll($"{tag1} {tag2} {tag3}").Length;
        await Assert.That(Count(arena, n => n.Is(tag1), n => n.Is(tag2), n => n.Is(tag3))).IsEqualTo(expected);
    }

    [Test]
    [MethodDataSource(nameof(Complex))]
    public async Task SameResultComplex(
        TopLevelSmoke.SelectorTestCase testCase,
        CompactDocumentLayout layout
    )
    {
        var (mutable, arena) = GetDocs(testCase.FileName, layout);
        var expected = mutable.QuerySelectorAll(testCase.CssSelector).Length;
        await Assert.That(Count(arena, CreateChain(testCase.CssSelector))).IsEqualTo(expected);
    }
}
#endif
