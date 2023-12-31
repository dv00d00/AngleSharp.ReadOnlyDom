using System;
using System.Linq;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.Helpers;
using AngleSharp.ReadOnlyDom.ReadOnly.Html;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[Config(typeof(Config))]
[MemoryDiagnoser]
public class ReadOnlySelectorsBenchmark
{
    private class Config : ManualConfig
    {
        public Config()
        {
            AddJob(Job.ShortRun
                .WithRuntime(CoreRuntime.Core80)
                .WithStrategy(RunStrategy.Throughput)
            );
        }
    }

    private readonly IHtmlDocument document = new HtmlParser().ParseDocument(StaticHtml.Github);
    private readonly IReadOnlyDocument documentReadonly = new HtmlParser(default, ReadOnlyParser.DefaultContext).ParseReadOnlyDocument(StaticHtml.Github);

    private static readonly Func<IReadOnlyNode, bool>[] _selectors = {
        n => n.TagClass("div", "edit-comment-hide"),
        n => n.TagClass("tr", "d-block"),
        n => n.TagClass("td", "comment-body")
    };

    [Benchmark]
    public void Selectors()
    {
        var _ = document
                .QuerySelectorAll("div.edit-comment-hide tr.d-block td.comment-body")
                .Count();
    }

    [Benchmark]
    public void SelectorsReadonly()
    {
        var _ = documentReadonly
                .QueryAll(_selectors)
                .Count();
    }
}