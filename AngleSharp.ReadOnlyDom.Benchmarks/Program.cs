#nullable enable
using System;
using System.Text;
using AngleSharp.Benchmarks.UserCode.Filters;
using BenchmarkDotNet.Running;

namespace AngleSharp.Benchmarks
{
    using System.Buffers;
    using System.Collections;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Net.Http;
    using System.Numerics;
    using System.Threading.Tasks;
    using Css.Dom;
    using Css.Parser;
    using Dom;
    using Html.Parser;
    using Microsoft.IO;
    using ReadOnly.Html;
    using Text;
    using UserCode;

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