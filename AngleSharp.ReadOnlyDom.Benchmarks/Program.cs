using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.Filters;
using AngleSharp.ReadOnlyDom.Helpers;
using AngleSharp.Text;
using BenchmarkDotNet.Running;

namespace AngleSharp.ReadOnlyDom.Benchmarks
{
    static class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Length > 0)
            {
                BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
                return;
            }

            var parser = new HtmlParser(new HtmlParserOptions(), ReadOnlyParser.DefaultContext);
            using var doc = parser.ParseReadOnlyDocument(StaticHtml.Github, new FirstTagAndAllChildren("body").Loop);
            var sb = new StringBuilder();

            var comments = doc?
                .QueryAll(
                    n => n.Tag("div") && n.Class("edit-comment-hide"),
                    n => n.Tag("tr") && n.Class("d-block"),
                    n => n.Tag("td") && n.Class("comment-body"))
                .Select(n => n.GetTextContent(sb, trimMode: TrimMode.Ends))
                .ToList();

            foreach (var comment in comments)
            {
                Console.WriteLine(comment);
                Console.WriteLine("========================================");
            }
        }
    }
}