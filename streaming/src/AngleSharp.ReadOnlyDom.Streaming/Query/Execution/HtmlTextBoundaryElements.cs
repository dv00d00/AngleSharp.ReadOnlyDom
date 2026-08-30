using System.Collections.Frozen;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;

namespace AngleSharp.ReadOnlyDom.Streaming.Query.Execution;

/// <summary>
/// Elements whose start and end tags separate words in normalized text. Text extraction that
/// ignores them concatenates across rendered line and cell boundaries, turning
/// <c>&lt;td&gt;12&lt;/td&gt;&lt;td&gt;34&lt;/td&gt;</c> into <c>1234</c>. Inline elements
/// (<c>span</c>, <c>b</c>, <c>a</c>, ...) are deliberately absent: browsers render
/// <c>&lt;b&gt;Hello&lt;/b&gt;&lt;i&gt;World&lt;/i&gt;</c> as one word and so do we.
/// </summary>
internal static class HtmlTextBoundaryElements
{
    private static readonly FrozenSet<ulong> Keys = new[]
    {
        // Flow content
        "address",
        "article",
        "aside",
        "blockquote",
        "center",
        "details",
        "dialog",
        "dir",
        "div",
        "dl",
        "fieldset",
        "figcaption",
        "figure",
        "footer",
        "form",
        "header",
        "hgroup",
        "hr",
        "main",
        "menu",
        "nav",
        "ol",
        "p",
        "pre",
        "section",
        "summary",
        "ul",
        // Headings
        "h1",
        "h2",
        "h3",
        "h4",
        "h5",
        "h6",
        // List and description items
        "dd",
        "dt",
        "li",
        // Tables
        "caption",
        "col",
        "colgroup",
        "table",
        "tbody",
        "td",
        "tfoot",
        "th",
        "thead",
        "tr",
        // Line break and other rendered separations
        "br",
        "legend",
        "optgroup",
        "option",
    }.Select(Compact).ToFrozenSet();

    /// <summary>
    /// Returns true when a tag separates words in normalized text. <paramref name="identityLength"/>
    /// is non-zero only for names that are not compact-representable, and no boundary element is
    /// spelled that way, so those names are rejected without touching their bytes.
    /// </summary>
    internal static bool IsBoundary(ulong identity, int identityLength) =>
        identityLength == 0 && Keys.Contains(identity);

    private static ulong Compact(string name)
    {
        Span<byte> ascii = stackalloc byte[name.Length];
        for (var index = 0; index < name.Length; index++)
            ascii[index] = (byte)name[index];
        if (!Utf8HtmlName.TryGetCompactKey(ascii, out var key))
            throw new InvalidOperationException("An HTML text-boundary name was not compact-representable.");
        return key;
    }
}
