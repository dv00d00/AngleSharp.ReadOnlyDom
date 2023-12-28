using System;
using System.Collections.Generic;
using AngleSharp.Html;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.Io;
using AngleSharp.ReadOnlyDom.Helpers;
using AngleSharp.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[Config(typeof(Config))]
[MemoryDiagnoser]
public class OverheadBenchmark
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

    public class HtmlTask
    {
        public required string Display { get; init; }
        public required string Html { get; init; }
        public required HtmlParserOptions Options {get; init;}
        public override string ToString() => Display;
    }

    [GlobalSetup]
    public void Setup()
    {
        HtmlEntityProvider.Resolver.GetSymbol("test");
        MimeTypeNames.FromExtension(".txt");
        
    }

    [ParamsSource(nameof(GetTasks))] public HtmlTask? It { get; set; }

    public IEnumerable<HtmlTask> GetTasks()
    {
        yield return new HtmlTask { Display = "br", Html = "<br/>", Options = default};
        yield return new HtmlTask { Display = "table", Html = StaticHtml.HtmlTable, Options = default};
        yield return new HtmlTask { Display = "table tabbed", Html = StaticHtml.HtmlTableTabbed, Options = default};
        yield return new HtmlTask { Display = "table TABBED", Html = StaticHtml.HtmlTableTabbedSoMuch, Options = default};
        yield return new HtmlTask { Display = "github", Html = StaticHtml.Github, Options = default};
        
        yield return new HtmlTask { Display = "br *", Html = "<br/>", Options = Custom };
        yield return new HtmlTask { Display = "table *", Html = StaticHtml.HtmlTable, Options = Custom };
        yield return new HtmlTask { Display = "table tabbed *", Html = StaticHtml.HtmlTableTabbed, Options = Custom };
        yield return new HtmlTask { Display = "table TABBED *", Html = StaticHtml.HtmlTableTabbedSoMuch, Options = Custom };
        yield return new HtmlTask { Display = "github *", Html = StaticHtml.Github, Options = Custom };
    }

    [Benchmark(Baseline = true)]
    public void V1()
    {
        var htmlParser = new HtmlParser(It!.Options, ReadOnlyParser.DefaultContext);
        var doc = htmlParser.ParseDocument(It!.Html);
        doc.Dispose();
    }

    [Benchmark]
    public void V2()
    {
        var htmlParser = new HtmlParser(It!.Options, ReadOnlyParser.DefaultContext);
        var doc = htmlParser.ParseReadOnlyDocument(It!.Html);
        doc.Dispose();
    }

    public static readonly HtmlParserOptions Custom = new HtmlParserOptions()
    {
        IsStrictMode = false,
        IsScripting = false,
        IsNotConsumingCharacterReferences = true,
        IsNotSupportingFrames = true,
        IsSupportingProcessingInstructions = false,
        IsEmbedded = false,

        IsKeepingSourceReferences = false,
        IsPreservingAttributeNames = false,
        IsAcceptingCustomElementsEverywhere = false,

        SkipScriptText = true,
        SkipRawText = true,
        SkipDataText = false,
        SkipComments = true,
        SkipPlaintext = true,
        SkipCDATA = true,
        SkipRCDataText = true,
        SkipProcessingInstructions = true,
        DisableElementPositionTracking = true,
        ShouldEmitAttribute = static (ref StructHtmlToken _, ReadOnlyMemory<Char> n) =>
        {
            var s = n.Span;
            return s.Length switch
            {
                2 => s[0] == 'i' && s[1] == 'd',
                _ => false
            };
        },
    };
}