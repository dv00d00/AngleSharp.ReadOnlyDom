﻿using System.Collections.Concurrent;
using AngleSharp.Html.Construction;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.Helpers;
using AngleSharp.ReadOnlyDom.ReadOnly.Html;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace AngleSharp.ReadOnly.Tests;

public class TopLevelSmoke
{
    private readonly ITestOutputHelper _testOutputHelper;

    const string baseDir =
        @"C:\Users\Dmitry\RiderProjects\AngleSharp.ReadOnlyDom\AngleSharp.ReadOnlyDom.Benchmarks\temp\";

    private static readonly ConcurrentDictionary<string, string> FileContents =
        new ConcurrentDictionary<string, string>();

    private static string GetHtml(string fileName) =>
        FileContents.GetOrAdd(fileName, k => File.ReadAllText(baseDir + k));

    private static readonly ConcurrentDictionary<string, IHtmlDocument> ParsedMutableDocs =
        new ConcurrentDictionary<string, IHtmlDocument>();

    private static readonly ConcurrentDictionary<string, IReadOnlyDocument> ParsedRoDocs =
        new ConcurrentDictionary<string, IReadOnlyDocument>();

    private static (IHtmlDocument, IReadOnlyDocument) GetDocs(string fileName)
    {
        var html = GetHtml(fileName);
        return (ParsedMutableDocs.GetOrAdd(fileName, k => parser.ParseDocument(html)),
            ParsedRoDocs.GetOrAdd(fileName, k => parser.ParseReadOnlyDocument(html)));
    }

    private static readonly string[] Tags =
        (new[]
        {
    "div", "span", "a", "p", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "li",
    "ol", "table", "tr", "td", "th", "tbody", "thead", "tfoot", "caption", "colgroup", "col", "img", "br", "hr",
    "form", "input", "button", "textarea", "select", "option", "optgroup", "label", "fieldset", "legend", "iframe",
    "script", "noscript", "style", "link", "meta", "title", "head", "body", "html", "base", "area", "map", "param",
    "object", "embed", "track", "audio", "video", "source", "canvas", "svg", "math", "del", "ins", "time", "mark",
    "progress", "meter", "details", "summary", "menuitem", "menu", "dialog", "slot", "template", "article", "aside",
    "bdi", "command", "datalist", "dfn", "figcaption", "figure", "footer", "header", "kbd", "main", "nav", "output",
    "picture", "ruby", "rp", "rt", "section", "small", "strong", "sub", "sup", "var", "wbr", "b", "i", "u", "s",
    "pre", "code", "q", "blockquote", "abbr", "cite", "em", "samp", "a", "bdo", "br", "button", "canvas", "cite",
    "code", "command", "datalist", "dfn", "em", "embed", "i", "iframe", "input", "kbd", "keygen", "label",
    "mark", "math", "meter", "noscript", "object", "output", "progress", "q", "ruby", "samp", "script", "select",
    "small", "span", "strong", "sub", "sup", "textarea", "time", "var", "wbr", "text", "acronym", "address",
    "big", "dt", "ins", "strike", "tt", "xmp", "applet", "basefont", "bgsound", "blink", "center", "command", "content",
    "text", "dir", "element", "font", "frame", "frameset", "image", "isindex", "keygen", "listing", "marquee",
    "rect", "shadow", "spacer", "template", "nextid", "noembed", "plaintext", "rb", "rtc", "section", "summary",
    "sup", "time", "track", "var", "wbr", "xmp", "abbr", "acronym", "address", "applet", "article", "aside", "audio",
        }).Distinct().ToArray();
    
    private static readonly string[] TagsShort =
        (new[]
        {
            "div", "span", "a", "p", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "li",
             "table", "tr", "td", "th", "tbody", "thead", "tfoot"
        }).Distinct().ToArray();

    public static IEnumerable<object[]> FilesPlusSingleTag() => Directory.EnumerateFiles(
            @"C:\Users\Dmitry\RiderProjects\AngleSharp.ReadOnlyDom\AngleSharp.ReadOnlyDom.Benchmarks\temp\")
        .SelectMany(path => Tags.Select(t => new object[] { Path.GetFileName(path), t }));
    
    public static IEnumerable<object[]> FilesPlusThreeTags() => Directory.EnumerateFiles(
            @"C:\Users\Dmitry\RiderProjects\AngleSharp.ReadOnlyDom\AngleSharp.ReadOnlyDom.Benchmarks\temp\")
        .SelectMany(path => 
            TagsShort.SelectMany(t1 => 
                TagsShort.Select(t2 => 
                
                new object[] { Path.GetFileName(path), t1, t2 })));

    public static HtmlParser parser = new HtmlParser(new HtmlParserOptions()
    {
        IsKeepingSourceReferences = true
    }, ReadOnlyParser.DefaultContext);

    public TopLevelSmoke(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    [Theory]
    [MemberData(nameof(FilesPlusSingleTag))]
    public void SameResult(string fileName, string tag)
    {
        var (mutable, ro) = GetDocs(fileName);
        var elements = mutable.QuerySelectorAll(tag).ToArray();
        var readOnlyNodes = ro.QueryAll(n => n.Tag(tag)).ToArray();
        var expected = elements.Length;
        var actual = readOnlyNodes.Length;

        actual.Should().Be(expected);
        
        // try
        // {
        //     
        // }
        // catch
        // {
        //     var existing = new HashSet<string>();
        //     foreach (var element in elements)
        //     {
        //         existing.Add(element.SourceReference.Position.ToString());
        //     }
        //
        //     foreach (var readOnlyNode in readOnlyNodes)
        //     {
        //         var message = ((IConstructableElement)readOnlyNode).SourceReference.Position.ToString();
        //         if (existing.Contains(message))
        //         {
        //             continue;
        //         }
        //
        //         _testOutputHelper.WriteLine(message!);
        //
        //         var text = new StringWriter();
        //         readOnlyNode.Print(text);
        //         _testOutputHelper.WriteLine(text.ToString());
        //     }
        //
        //     var groups = readOnlyNodes
        //         .GroupBy(item => ((IConstructableElement)item).SourceReference.Position.ToString())
        //         .Where(it => it.Count() > 1).ToArray();
        //
        //     foreach (var group in groups)
        //     {
        //         foreach (var readOnlyNode in group)
        //         {
        //             var message = ((IConstructableElement)readOnlyNode).SourceReference.Position.ToString();
        //             _testOutputHelper.WriteLine(message!);
        //
        //             _testOutputHelper.WriteLine(readOnlyNode.NodeName.ToString());
        //
        //             var text = new StringWriter();
        //             readOnlyNode.Print(text);
        //             _testOutputHelper.WriteLine(text.ToString());
        //         }
        //
        //         _testOutputHelper.WriteLine("====================================");
        //     }
        //
        //     throw;
        // }
    }
    
    [Theory]
    [MemberData(nameof(FilesPlusThreeTags))]
    public void SameResult2(string fileName, string tag1, string tag2)
    {
        var (mutable, ro) = GetDocs(fileName);
        var elements = mutable.QuerySelectorAll(tag1 + " " + tag2 ).ToArray();
        var readOnlyNodes = ro.QueryAll(n => n.Tag(tag1), n => n.Tag(tag2)).ToArray();
        var expected = elements.Length;
        var actual = readOnlyNodes.Length;
        actual.Should().Be(expected);
        // try
        // {
        //    
        // }
        // catch
        // {
        //     var existing = new HashSet<string>();
        //     foreach (var element in elements)
        //     {
        //         existing.Add(element.SourceReference.Position.ToString());
        //     }
        //
        //     foreach (var readOnlyNode in readOnlyNodes)
        //     {
        //         var message = ((IConstructableElement)readOnlyNode).SourceReference.Position.ToString();
        //         if (existing.Contains(message))
        //         {
        //             continue;
        //         }
        //
        //         _testOutputHelper.WriteLine(message!);
        //
        //         var text = new StringWriter();
        //         readOnlyNode.Print(text);
        //         _testOutputHelper.WriteLine(text.ToString());
        //     }
        //
        //     var groups = readOnlyNodes
        //         .GroupBy(item => ((IConstructableElement)item).SourceReference.Position.ToString())
        //         .Where(it => it.Count() > 1).ToArray();
        //
        //     foreach (var group in groups)
        //     {
        //         foreach (var readOnlyNode in group)
        //         {
        //             var message = ((IConstructableElement)readOnlyNode).SourceReference.Position.ToString();
        //             _testOutputHelper.WriteLine(message!);
        //
        //             _testOutputHelper.WriteLine(readOnlyNode.NodeName.ToString());
        //
        //             var text = new StringWriter();
        //             readOnlyNode.Print(text);
        //             _testOutputHelper.WriteLine(text.ToString());
        //         }
        //
        //         _testOutputHelper.WriteLine("====================================");
        //     }
        //
        //     throw;
        // }
    }
}