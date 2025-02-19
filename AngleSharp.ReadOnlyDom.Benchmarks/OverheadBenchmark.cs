using System;
using System.Collections.Generic;
using AngleSharp.Html;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.Io;
using AngleSharp.ReadOnlyDom.Filters;
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
            AddJob(Job.MediumRun
                .WithRuntime(CoreRuntime.Core80)
                .WithStrategy(RunStrategy.Throughput)
            );
        }
    }

    public class HtmlTask
    {
        public required string Display { get; init; }
        public required string Html { get; init; }
        public required bool CustomOptions {get; init;}
        public override string ToString() => Display;
    }

    [GlobalSetup]
    public void Setup()
    {
        HtmlEntityProvider.Resolver.GetSymbol("test");
        MimeTypeNames.FromExtension(".txt");
        
    }

    [ParamsSource(nameof(GetTasks))] public HtmlTask It { get; set; } = null!;

    private static readonly HtmlParserOptions Custom = new HtmlParserOptions()
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
        ShouldEmitAttribute = static (ref StructHtmlToken token, ReadOnlyMemory<char> attributeName) =>
        {
            if (token.Name != "div")
                return false;
            
            var s = attributeName.Span;
            return s.Length switch
            {
                2 => s[0] == 'i' && s[1] == 'd',
                5 => s[0] == 'c' && s[1] == 'l' && s[2] == 'a' && s[3] == 's' && s[4] == 's',
                _ => false
            };
        },
    };

    public IEnumerable<HtmlTask> GetTasks()
    {
        yield return new HtmlTask { Display = "github", Html = StaticHtml.Github, CustomOptions = false };
        yield return new HtmlTask { Display = "github *", Html = StaticHtml.Github, CustomOptions = true };
    }
    
    private static readonly HtmlParser DefaultParser = new HtmlParser(default, ReadOnlyParser.DefaultContext);
    private static readonly HtmlParser CustomParser = new HtmlParser(Custom, ReadOnlyParser.DefaultContext);

    [Benchmark(Baseline = true)]
    public void V1()
    {
        var htmlParser = It.CustomOptions ? CustomParser : DefaultParser;
        var doc = htmlParser.ParseDocument(It.Html);
        doc.Dispose();
    }

    [Benchmark]
    public void V2()
    {
        var htmlParser = It.CustomOptions ? CustomParser : DefaultParser;
        var doc = htmlParser.ParseReadOnlyDocument(It.Html, new FirstTagAndAllChildren("body").Loop);
        doc.Dispose();
    }
}