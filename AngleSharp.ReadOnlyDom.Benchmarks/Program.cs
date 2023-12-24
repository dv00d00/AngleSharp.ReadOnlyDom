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
        static async Task Main(String[] args)
        {
            await Task.CompletedTask;

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            if (args.Length > 0)
            {
                BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
                return;
            }

            var html = new PrefetchedTextSource(StaticHtml.Github);
            var parser = new HtmlParser();
            using var doc = parser.ParseReadOnlyDocument(html, new FirstTagAndAllChildren("body").Loop);
            var sb = new StringBuilder();

            var comments = doc?
                .QueryAll(
                    n => n.Tag("div") && n.Class("edit-comment-hide"),
                    n => n.Tag("tr") && n.Class("d-block"),
                    n => n.Tag("td") && n.Class("comment-body"))
                .Select(n => n.GetTextContent(sb))
                .ToList();

            foreach (var comment in comments)
            {
                Console.WriteLine(comment);
                Console.WriteLine("========================================");
            }
        }
    }
}