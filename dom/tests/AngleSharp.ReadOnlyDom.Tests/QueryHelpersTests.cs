using System.Collections;
using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom;
using AngleSharp.ReadOnlyDom.Html;

namespace AngleSharp.Readonly.Tests;

public class QueryHelpersTests
{
    [Test]
    [Arguments("\t")]
    [Arguments("\n")]
    [Arguments("\f")]
    [Arguments("\r")]
    [Arguments(" ")]
    public async Task ClassQueriesUseOnlyHtmlWhitespaceSeparators(string separator)
    {
        var parser = new HtmlParser(default, ReadOnlyParser.DefaultContext);
        using var document = parser.ParseReadOnlyDocument($"<p id=content class='alpha{separator}beta'></p>");
        var content = document.QueryOne(static node => node.TagId("p", "content"))!;

        await Assert.That(content.Class("alpha")).IsTrue();
        await Assert.That(content.Class("beta")).IsTrue();
        await Assert.That(content.Classes("alpha", "beta")).IsTrue();
    }

    [Test]
    public async Task ClassQueriesKeepNbspInsideTokenAndRejectEmptyToken()
    {
        const string joined = "alpha\u00a0beta";
        var parser = new HtmlParser(default, ReadOnlyParser.DefaultContext);
        using var document = parser.ParseReadOnlyDocument($"<p id=content class='{joined}'></p>");
        var content = document.QueryOne(static node => node.TagId("p", "content"))!;

        await Assert.That(content.Class("alpha")).IsFalse();
        await Assert.That(content.Class("beta")).IsFalse();
        await Assert.That(content.Class(joined)).IsTrue();
        await Assert.That(content.Class("")).IsFalse();
        await Assert.That(content.Classes("", "")).IsFalse();
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments(" \t\r\n")]
    public async Task TrimEndsRemovesAllWhitespaceOnlyText(string text)
    {
        var parser = new HtmlParser(default, ReadOnlyParser.DefaultContext);
        using var document = parser.ParseReadOnlyDocument($"<span id=content>{text}</span>");
        var content = document.QueryOne(static node => node.TagId("span", "content"))!;

        await Assert.That(content.GetTextContent(TrimMode.Ends)).IsEqualTo(string.Empty);
    }

    [Test]
    [Arguments("value")]
    [Arguments("value ")]
    [Arguments("value \t\r\n")]
    public async Task TrimEndsRemovesAnyTrailingWhitespace(string text)
    {
        var parser = new HtmlParser(default, ReadOnlyParser.DefaultContext);
        using var document = parser.ParseReadOnlyDocument($"<span id=content>{text}</span>");
        var content = document.QueryOne(static node => node.TagId("span", "content"))!;

        await Assert.That(content.GetTextContent(TrimMode.Ends)).IsEqualTo("value");
    }

    [Test]
    public async Task TrimEndsAppliesToCombinedTextContentAcrossNodes()
    {
        var parser = new HtmlParser(default, ReadOnlyParser.DefaultContext);
        using var document = parser.ParseReadOnlyDocument(
            "<span id=content> \t<strong> </strong> value <em> </em> \r\n</span>"
        );
        var content = document.QueryOne(static node => node.TagId("span", "content"))!;

        await Assert.That(content.GetTextContent(TrimMode.Ends)).IsEqualTo("value");
    }

    [Test]
    public async Task CountTagClassCountsDescendantsButNotReceiver()
    {
        var parser = new HtmlParser(default, ReadOnlyParser.DefaultContext);
        using var document = parser.ParseReadOnlyDocument(
            "<section id=root class=target><section class=target></section><div><section class='other target'></section></div></section>"
        );
        var root = document.QueryOne(static node => node.TagId("section", "root"))!;

        await Assert.That(root.CountTagClass("section", "target")).IsEqualTo(2);
    }

    [Test]
    public async Task CountTagClassTraversesAnyReadOnlyNodeImplementationWithoutRecursion()
    {
        var parser = new HtmlParser(default, ReadOnlyParser.DefaultContext);
        using var document = parser.ParseReadOnlyDocument("<section class=target></section>");
        IReadOnlyNode current = document.QueryOne(static node => node.TagClass("section", "target"))!;

        for (var depth = 0; depth < 10_000; depth++)
            current = new TestNode(current);

        var root = new TestNode(current);

        await Assert.That(root.CountTagClass("section", "target")).IsEqualTo(1);
    }

    private sealed class TestNode(IReadOnlyNode child) : IReadOnlyNode
    {
        private readonly IReadOnlyNodeList _children = new TestNodeList(child);

        public StringOrMemory NodeName => "test";
        public NodeFlags Flags => NodeFlags.None;
        public IReadOnlyNode? Parent => null;
        public IReadOnlyNodeList ChildNodes => _children;

        public void Print(TextWriter writer) { }
    }

    private sealed class TestNodeList(IReadOnlyNode child) : IReadOnlyNodeList
    {
        public IReadOnlyNode this[int index] =>
            index == 0 ? child : throw new ArgumentOutOfRangeException(nameof(index));

        public int Length => 1;

        public IEnumerator<IReadOnlyNode> GetEnumerator()
        {
            yield return child;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
