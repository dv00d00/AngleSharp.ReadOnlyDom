using System.Buffers;
using System.Collections.Concurrent;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom;
using AngleSharp.ReadOnlyDom.Html;

namespace AngleSharp.Readonly.Tests;

// dont run from IDE, will generate 166K test cases
// dotnet test --configuration Release
public class TopLevelSmoke
{
    const int MaxSize = (512 + 128) * 1024;

    const string BaseDir = @".\temp\";

    private static readonly ConcurrentDictionary<string, string> FileContents = new();
    private static readonly ConcurrentDictionary<string, IHtmlDocument> ParsedMutableDocs = new();
    private static readonly ConcurrentDictionary<string, IReadOnlyDocument> ParsedRoDocs = new();

    private static string GetHtml(string fileName) =>
        FileContents.GetOrAdd(fileName, static fileName => File.ReadAllText(BaseDir + fileName));

    private static (IHtmlDocument, IReadOnlyDocument) GetDocs(string fileName)
    {
        return (
            ParsedMutableDocs.GetOrAdd(fileName, static fileName => parser.ParseDocument(GetHtml(fileName))),
            ParsedRoDocs.GetOrAdd(fileName, static fileName => parser.ParseReadOnlyDocument(GetHtml(fileName)))
        );
    }

    private static readonly string[] Tags = (
        new[]
        {
            "div",
            "span",
            "a",
            "p",
            "h1",
            "h2",
            "h3",
            "h4",
            "h5",
            "h6",
            "ul",
            "li",
            "ol",
            "table",
            "tr",
            "td",
            "th",
            "tbody",
            "thead",
            "tfoot",
            "caption",
            "colgroup",
            "col",
            "img",
            "br",
            "hr",
            "form",
            "input",
            "button",
            "textarea",
            "select",
            "option",
            "optgroup",
            "label",
            "fieldset",
            "legend",
            "iframe",
            "script",
            "noscript",
            "style",
            "link",
            "meta",
            "title",
            "head",
            "body",
            "html",
            "base",
            "area",
            "map",
            "param",
            "object",
            "embed",
            "track",
            "audio",
            "video",
            "source",
            "canvas",
            "svg",
            "math",
            "del",
            "ins",
            "time",
            "mark",
            "progress",
            "meter",
            "details",
            "summary",
            "menuitem",
            "menu",
            "dialog",
            "slot",
            "template",
            "article",
            "aside",
            "bdi",
            "command",
            "datalist",
            "dfn",
            "figcaption",
            "figure",
            "footer",
            "header",
            "kbd",
            "main",
            "nav",
            "output",
            "picture",
            "ruby",
            "rp",
            "rt",
            "section",
            "small",
            "strong",
            "sub",
            "sup",
            "var",
            "wbr",
            "b",
            "i",
            "u",
            "s",
            "pre",
            "code",
            "q",
            "blockquote",
            "abbr",
            "cite",
            "em",
            "samp",
            "a",
            "bdo",
            "br",
            "button",
            "canvas",
            "cite",
            "code",
            "command",
            "datalist",
            "dfn",
            "em",
            "embed",
            "i",
            "iframe",
            "input",
            "kbd",
            "keygen",
            "label",
            "mark",
            "math",
            "meter",
            "noscript",
            "object",
            "output",
            "progress",
            "q",
            "ruby",
            "samp",
            "script",
            "select",
            "small",
            "span",
            "strong",
            "sub",
            "sup",
            "textarea",
            "time",
            "var",
            "wbr",
            "text",
            "acronym",
            "address",
            "big",
            "dt",
            "ins",
            "strike",
            "tt",
            "xmp",
            "applet",
            "basefont",
            "bgsound",
            "blink",
            "center",
            "command",
            "content",
            "text",
            "dir",
            "element",
            "font",
            "frame",
            "frameset",
            "image",
            "isindex",
            "keygen",
            "listing",
            "marquee",
            "rect",
            "shadow",
            "spacer",
            "template",
            "nextid",
            "noembed",
            "plaintext",
            "rb",
            "rtc",
            "section",
            "summary",
            "sup",
            "time",
            "track",
            "var",
            "wbr",
            "xmp",
            "abbr",
            "acronym",
            "address",
            "applet",
            "article",
            "aside",
            "audio",
        }
    )
        .Distinct()
        .ToArray();

    private static readonly string[] TagsShort = ["div", "span", "ol", "ul", "li", "table", "tr", "td"];

    public static IEnumerable<(string FileName, string Tag)> SingleTag() =>
        Directory
            .EnumerateFiles(BaseDir)
            .Where(it => new FileInfo(it).Length < MaxSize)
            .SelectMany(path => Tags.Select(t => (Path.GetFileName(path), t)));

    public static IEnumerable<(string FileName, string Tag1, string Tag2)> TwoTags() =>
        Directory
            .EnumerateFiles(BaseDir)
            .Where(it => new FileInfo(it).Length < MaxSize)
            .SelectMany(path => TagsShort.SelectMany(t1 => TagsShort.Select(t2 => (Path.GetFileName(path), t1, t2))));

    public static IEnumerable<(string FileName, string Tag1, string Tag2, string Tag3)> ThreeTags() =>
        Directory
            .EnumerateFiles(BaseDir)
            .Where(it => new FileInfo(it).Length < MaxSize)
            .SelectMany(path =>
                TagsShort.SelectMany(t1 =>
                    TagsShort.SelectMany(t2 => TagsShort.Select(t3 => (Path.GetFileName(path), t1, t2, t3)))
                )
            );

    public class SelectorTestCase
    {
        public required string FileName { get; set; }
        public required string CssSelector { get; set; }
        public required Func<IReadOnlyNode, bool>[] Chain { get; set; }

        public override string ToString()
        {
            return $"{FileName} {CssSelector}";
        }

        public SelectorTestCase? Combine(SelectorTestCase other)
        {
            if (other.FileName != FileName)
                return null;

            return new SelectorTestCase
            {
                FileName = FileName,
                CssSelector = CssSelector + " " + other.CssSelector,
                Chain = Chain.Concat(other.Chain).ToArray(),
            };
        }
    }

    private static IEnumerable<SelectorTestCase> GetTestCases(string file, string tag, string? id, string[] classes)
    {
        file = Path.GetFileName(file);

        yield return new SelectorTestCase
        {
            FileName = file,
            CssSelector = tag,
            Chain = [n => n.Tag(tag)],
        };

        id =
            id.IsNullOrWhiteSpace()
            || id is "19ee99feeb254bf99a88146643d1afa2" or "19ee99feeb254bf99a88146643d1afa3"
            || HasBadChar(id.AsSpan())
                ? null
                : id;

        if (id != null)
        {
            yield return new SelectorTestCase
            {
                FileName = file,
                CssSelector = "#" + id,
                Chain = [n => n.Id(id)],
            };

            yield return new SelectorTestCase
            {
                FileName = file,
                CssSelector = tag + "#" + id,
                Chain = [n => n.TagId(tag, id)],
            };
        }

        foreach (var @class in classes)
        {
            yield return new SelectorTestCase
            {
                FileName = file,
                CssSelector = "." + @class,
                Chain = [n => n.Class(@class)],
            };

            yield return new SelectorTestCase
            {
                FileName = file,
                CssSelector = tag + "." + @class,
                Chain = [n => n.TagClass(tag, @class)],
            };

            if (id != null)
            {
                yield return new SelectorTestCase
                {
                    FileName = file,
                    CssSelector = "#" + id + "." + @class,
                    Chain = [n => n.Id(id) && n.Class(@class)],
                };

                yield return new SelectorTestCase
                {
                    FileName = file,
                    CssSelector = tag + "#" + id + "." + @class,
                    Chain = [n => n.TagId(tag, id) && n.Class(@class)],
                };
            }
        }
    }

#if NETFRAMEWORK
    private static readonly char[] badName = ":()[]%/.! ?&'\",".ToCharArray();

    private static bool HasBadChar(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (Array.IndexOf(badName, c) >= 0)
                return true;
        }

        return false;
    }
#else
    private static readonly SearchValues<char> badName = SearchValues.Create(":()[]%/.! ?&'\",");

    private static bool HasBadChar(ReadOnlySpan<char> value) => value.ContainsAny(badName);
#endif

    public static IEnumerable<SelectorTestCase> Core()
    {
        return Directory
            .EnumerateFiles(BaseDir)
            .Where(it => new FileInfo(it).Length < MaxSize)
            .SelectMany(file =>
            {
                var fileName = Path.GetFileName(file);
                var html = GetHtml(fileName);
                var doc = ParsedMutableDocs.GetOrAdd(fileName, static (_, html) => parser.ParseDocument(html), html);

                return doc
                    .All.Where(it => TagsShort.Contains(it.LocalName))
                    .SelectMany(it =>
                    {
                        var classes = it.ClassList.Where(className => !HasBadChar(className.AsSpan())).ToArray();
                        return GetTestCases(file, it.LocalName, it.Id, classes);
                    });
            });
    }

    public static IEnumerable<SelectorTestCase> CustomSelectors()
    {
        return Core();
    }

    public static IEnumerable<SelectorTestCase> CustomSelectorsZip2()
    {
        var single = Core().ToArray();

        return single.Zip(single.Skip(1)).Select(it => it.First.Combine(it.Second)).Where(it => it != null)!;
    }

    public static IEnumerable<SelectorTestCase> CustomSelectorsZip3()
    {
        var single = Core().ToArray();

        return single
            .Zip(single.Skip(1), single.Skip(2))
            .Select(it => it.First.Combine(it.Second)?.Combine(it.Third))
            .Where(it => it != null)!;
    }

    public static IEnumerable<SelectorTestCase> AllComplexSelectors() =>
        CustomSelectors().Concat(CustomSelectorsZip2()).Concat(CustomSelectorsZip3());

    public static IEnumerable<(string FileName, string ClassName)> Classes() =>
        Directory
            .EnumerateFiles(BaseDir)
            .Where(it => new FileInfo(it).Length < MaxSize)
            .SelectMany(file =>
            {
                var fileName = Path.GetFileName(file);
                var html = GetHtml(fileName);
                var doc = ParsedMutableDocs.GetOrAdd(fileName, k => parser.ParseDocument(html));
                return doc
                    .All.SelectMany(n => n.ClassList)
                    .Distinct()
                    .Where(className => !HasBadChar(className.AsSpan()))
                    .Select(className => (fileName, className));
            });

    public static IEnumerable<(string FileName, string Id)> Ids() =>
        Directory
            .EnumerateFiles(BaseDir)
            .Where(it => new FileInfo(it).Length < MaxSize)
            .SelectMany(file =>
            {
                var fileName = Path.GetFileName(file);
                var html = GetHtml(fileName);
                var doc = ParsedMutableDocs.GetOrAdd(fileName, k => parser.ParseDocument(html));
                return doc
                    .All.Select(it => it.Id)
                    .Where(id => !id.IsNullOrWhiteSpace() && !HasBadChar(id.AsSpan()))
                    .Distinct()
                    .Take(75)
                    .Select(id => (fileName, id!));
            });

    public static HtmlParser parser = new HtmlParser(
        new HtmlParserOptions() { IsKeepingSourceReferences = true },
        ReadOnlyParser.DefaultContext
    );

    [Test]
    [MethodDataSource(nameof(SingleTag))]
    public async Task SameResultTag(string fileName, string tag)
    {
        var (mutable, ro) = GetDocs(fileName);
        var elements = mutable.QuerySelectorAll(tag).ToArray();
        var readOnlyNodes = ro.QueryAll(n => n.Tag(tag)).ToArray();
        var expected = elements.Length;
        var actual = readOnlyNodes.Length;
        await Assert.That(actual).IsEqualTo(expected);
    }

    [Test]
    [MethodDataSource(nameof(Classes))]
    public async Task SameResultClass(string fileName, string class1)
    {
        var (mutable, ro) = GetDocs(fileName);
        var elements = mutable.QuerySelectorAll($".{class1}").ToArray();
        var readOnlyNodes = ro.QueryAll(n => n.Class(class1)).ToArray();
        var expected = elements.Length;
        var actual = readOnlyNodes.Length;
        try
        {
            await Assert.That(actual).IsEqualTo(expected);
        }
        catch (Exception)
        {
            var missing = elements.Where(it =>
                !readOnlyNodes.Any(ron => ((IReadOnlyElement)ron).SourceReference == it.SourceReference)
            );
            foreach (var element in missing)
            {
                TestContext.Current!.Output.WriteLine(element.SourceReference!.ToString());
                TestContext.Current!.Output.WriteLine(element.OuterHtml);
                TestContext.Current!.Output.WriteLine("=============================");
            }
            throw;
        }
    }

    [Test]
    [MethodDataSource(nameof(Ids))]
    public async Task SameResultId(string fileName, string id)
    {
        if (id is "19ee99feeb254bf99a88146643d1afa2" or "19ee99feeb254bf99a88146643d1afa3")
            return;

        var (mutable, ro) = GetDocs(fileName);
        var elements = mutable.QuerySelectorAll($"#{id}").ToArray();
        var readOnlyNodes = ro.QueryAll(n => n.Id(id)).ToArray();
        var expected = elements.Length;
        var actual = readOnlyNodes.Length;

        try
        {
            await Assert.That(actual).IsEqualTo(expected);
        }
        catch (Exception)
        {
            var missing = elements.Where(it =>
                readOnlyNodes.All(ron => ((IReadOnlyElement)ron).SourceReference != it.SourceReference)
            );
            foreach (var element in missing)
            {
                TestContext.Current!.Output.WriteLine(element.SourceReference!.ToString());
                TestContext.Current!.Output.WriteLine(element.OuterHtml);
                TestContext.Current!.Output.WriteLine("=============================");
            }
            throw;
        }
    }

    [Test]
    [MethodDataSource(nameof(TwoTags))]
    public async Task SameResultTwoTags(string fileName, string tag1, string tag2)
    {
        var (mutable, ro) = GetDocs(fileName);
        var elements = mutable.QuerySelectorAll($"{tag1} {tag2}").ToArray();
        var readOnlyNodes = ro.QueryAll(n => n.Tag(tag1), n => n.Tag(tag2)).ToArray();
        var expected = elements.Length;
        var actual = readOnlyNodes.Length;
        await Assert.That(actual).IsEqualTo(expected);
    }

    [Test]
    [MethodDataSource(nameof(ThreeTags))]
    public async Task SameResultThreeTags(string fileName, string tag1, string tag2, string tag3)
    {
        var (mutable, ro) = GetDocs(fileName);
        var elements = mutable.QuerySelectorAll($"{tag1} {tag2} {tag3}").ToArray();
        var readOnlyNodes = ro.QueryAll(n => n.Tag(tag1), n => n.Tag(tag2), n => n.Tag(tag3)).ToArray();
        var expected = elements.Length;
        var actual = readOnlyNodes.Length;
        await Assert.That(actual).IsEqualTo(expected);
    }

    [Test]
    [MethodDataSource(nameof(AllComplexSelectors))]
    public async Task SameResultComplex(SelectorTestCase testCase)
    {
        var (mutable, ro) = GetDocs(testCase.FileName);
        var elements = mutable.QuerySelectorAll(testCase.CssSelector).ToArray();
        var readOnlyNodes = ro.QueryAll(testCase.Chain).ToArray();
        var expected = elements.Length;
        var actual = readOnlyNodes.Length;

        try
        {
            await Assert.That(actual).IsEqualTo(expected);
        }
        catch (Exception)
        {
            var missing = elements.Where(it =>
                readOnlyNodes.All(ron => ((IReadOnlyElement)ron).SourceReference != it.SourceReference)
            );
            foreach (var element in missing)
            {
                TestContext.Current!.Output.WriteLine(element.SourceReference!.ToString());
                TestContext.Current!.Output.WriteLine(element.OuterHtml);
                TestContext.Current!.Output.WriteLine("=============================");
            }
            throw;
        }
    }
}
