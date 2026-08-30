using System.Text;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.Streaming.Query;
using FsCheck;
using FsCheck.Fluent;
using Element = AngleSharp.ReadOnlyDom.Streaming.Query.Element;

namespace AngleSharp.Readonly.Tests;

public sealed class NestedDivPropertyTests
{
    [Test]
    public void GeneratedNestedDivTextTotalsMatchAngleSharpMutableDomAcrossActiveDepth()
    {
        var plan = StreamQuery
            .For<DivTextTotals>("div")
            .OnStart(static (ref DivTextTotals state, in Element _) => state.Start())
            .OnText(static (ref DivTextTotals state, ReadOnlySpan<byte> text) => state.Text(text))
            .OnEnd(static (ref DivTextTotals state) => state.End())
            .Compile();

        var property = Prop.ForAll(
            WellFormedNestedDivMarkup().ToArbitrary(),
            source =>
            {
                using var mutable = new HtmlParser().ParseDocument(source);
                var expected = mutable.QuerySelectorAll("div").Select(static div => div.TextContent.Length).ToList();
                var actual = plan.Execute(Encoding.UTF8.GetBytes(source), new DivTextTotals()).Lengths;
                if (!expected.SequenceEqual(actual))
                    throw new InvalidOperationException(
                        $"Div text-length totals diverged for generated HTML:\n{Escape(source)}"
                    );
            }
        );
        Check.One(Config.QuickThrowOnFailure.WithMaxTest(10_000), property);
    }

    private static Gen<string> WellFormedNestedDivMarkup() =>
        from opCount in Gen.Choose(4, 40)
        from ops in Gen.Elements("open", "close", "text").ListOf(opCount)
        select BuildWellFormedMarkup(ops);

    private static string BuildWellFormedMarkup(IEnumerable<string> ops)
    {
        var markup = new StringBuilder();
        var depth = 0;
        var textCounter = 0;
        foreach (var op in ops)
        {
            switch (op)
            {
                case "open" when depth < 6:
                    markup.Append("<div>");
                    depth++;
                    break;
                case "close" when depth > 0:
                    markup.Append("</div>");
                    depth--;
                    break;
                case "text":
                    markup.Append('t').Append(textCounter++).Append(' ');
                    break;
            }
        }
        while (depth-- > 0)
            markup.Append("</div>");
        return markup.ToString();
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\0", "\\0").Replace("\r", "\\r").Replace("\n", "\\n");

    private sealed class DivTextTotals
    {
        private readonly List<int> _active = [];
        public List<int> Lengths { get; } = [];

        public void Start()
        {
            _active.Add(Lengths.Count);
            Lengths.Add(0);
        }

        public void Text(ReadOnlySpan<byte> utf8)
        {
            foreach (var index in _active)
                Lengths[index] += utf8.Length;
        }

        public void End() => _active.RemoveAt(_active.Count - 1);
    }
}
