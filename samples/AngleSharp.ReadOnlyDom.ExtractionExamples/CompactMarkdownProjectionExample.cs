using System.Text;
using AngleSharp.ReadOnlyDom.Compact.Document;
using AngleSharp.ReadOnlyDom.Compact.Parsing;
using AngleSharp.ReadOnlyDom.Compact.Query;

internal static class CompactMarkdownProjectionExample
{
    internal static string ProjectArticle(string html, string id, params string[] excludedElements)
    {
        var parser = CompactParser.CreateParser();
        using var document = parser.ParseCompactDocument(html);
        var article = document.Elements("article").WithAttribute("id", id).First();
        return article.Exists ? new Writer(excludedElements).Project(article) : String.Empty;
    }

    private sealed class Writer
    {
        private readonly string[] _excluded;
        private readonly StringBuilder _output = new();
        private bool _pendingSpace;

        internal Writer(IEnumerable<string> excludedElements)
        {
            _excluded = excludedElements
                .Concat(["script", "style", "template"])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        internal string Project(Node root)
        {
            VisitChildren(root, inPre: false);
            return _output.ToString().Trim();
        }

        private void Visit(Node node, bool inPre)
        {
            if (node.Kind == CompactNodeKind.Text)
            {
                if (inPre)
                    _output.Append(node.Text());
                else
                    AppendNormalized(node.Text());
                return;
            }
            if (!node.IsElement || IsExcluded(node.LocalName))
                return;

            var tag = node.LocalName;
            if (TryHeadingLevel(tag, out var level))
            {
                EnsureNewlines(2);
                _output.Append('#', level).Append(' ');
                VisitChildren(node, inPre: false);
                EnsureNewlines(2);
            }
            else if (tag.Equals("p", StringComparison.OrdinalIgnoreCase))
            {
                EnsureNewlines(2);
                VisitChildren(node, inPre: false);
                EnsureNewlines(2);
            }
            else if (tag.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                EnsureNewlines(1);
            }
            else if (tag.Equals("ul", StringComparison.OrdinalIgnoreCase) || tag.Equals("ol", StringComparison.OrdinalIgnoreCase))
            {
                EnsureNewlines(2);
                VisitChildren(node, inPre: false);
                EnsureNewlines(2);
            }
            else if (tag.Equals("li", StringComparison.OrdinalIgnoreCase))
            {
                EnsureNewlines(1);
                _output.Append("- ");
                VisitChildren(node, inPre: false);
                EnsureNewlines(1);
            }
            else if (tag.Equals("strong", StringComparison.OrdinalIgnoreCase) || tag.Equals("b", StringComparison.OrdinalIgnoreCase))
            {
                FlushPendingSpace();
                _output.Append("**");
                VisitChildren(node, inPre: false);
                _output.Append("**");
            }
            else if (tag.Equals("em", StringComparison.OrdinalIgnoreCase) || tag.Equals("i", StringComparison.OrdinalIgnoreCase))
            {
                FlushPendingSpace();
                _output.Append('*');
                VisitChildren(node, inPre: false);
                _output.Append('*');
            }
            else if (tag.Equals("a", StringComparison.OrdinalIgnoreCase))
            {
                FlushPendingSpace();
                _output.Append('[');
                VisitChildren(node, inPre: false);
                _output.Append(']');
                if (node.HasAttr("href"))
                    _output.Append('(').Append(node.Attr("href")).Append(')');
            }
            else if (tag.Equals("pre", StringComparison.OrdinalIgnoreCase))
            {
                EnsureNewlines(2);
                _output.Append("```text\n");
                VisitChildren(node, inPre: true);
                EnsureNewlines(1);
                _output.Append("```");
                EnsureNewlines(2);
            }
            else if (tag.Equals("code", StringComparison.OrdinalIgnoreCase) && !inPre)
            {
                FlushPendingSpace();
                _output.Append('`');
                VisitChildren(node, inPre: true);
                _output.Append('`');
            }
            else
            {
                VisitChildren(node, inPre);
            }
        }

        private void VisitChildren(Node node, bool inPre)
        {
            foreach (var child in node.Children())
                Visit(child, inPre);
        }

        private bool IsExcluded(ReadOnlySpan<char> tag)
        {
            foreach (var value in _excluded)
            {
                if (tag.Equals(value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private void AppendNormalized(ReadOnlySpan<char> text)
        {
            foreach (var character in text)
            {
                if (char.IsWhiteSpace(character))
                {
                    _pendingSpace = _output.Length != 0;
                    continue;
                }
                FlushPendingSpace();
                _output.Append(character);
            }
        }

        private void FlushPendingSpace()
        {
            if (_pendingSpace && _output.Length != 0 && !char.IsWhiteSpace(_output[^1]))
                _output.Append(' ');
            _pendingSpace = false;
        }

        private void EnsureNewlines(int count)
        {
            _pendingSpace = false;
            var present = 0;
            for (var index = _output.Length - 1; index >= 0 && _output[index] == '\n'; index--)
                present++;
            while (present++ < count)
                _output.Append('\n');
        }

        private static bool TryHeadingLevel(ReadOnlySpan<char> tag, out int level)
        {
            if (tag.Length == 2 && (tag[0] == 'h' || tag[0] == 'H') && tag[1] is >= '1' and <= '6')
            {
                level = tag[1] - '0';
                return true;
            }
            level = 0;
            return false;
        }
    }
}
