#if NET10_0
using System.Text;
using AngleSharp.Common;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.ReadOnlyDom.CompactPrototype;
using AngleSharp.ReadOnlyDom.Filters;
using AngleSharp.ReadOnlyDom.Html;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[MemoryDiagnoser]
public class CompactQueryWorkloadBenchmark
{
    private const string TargetId = "selected-region";
    private readonly HtmlParser _readOnlyParser;
    private readonly DirectCompactParserSession _directParser;
    private readonly HtmlParser _readOnlyTextParser;
    private readonly DirectCompactParserSession _directTextParser;
    private readonly string _targetPage = CreateTargetPage();
    private readonly string _textPage = CreateTextPage();

    public CompactQueryWorkloadBenchmark()
    {
        var selectedOptions = CreateSelectedOptions();
        _readOnlyParser = new HtmlParser(selectedOptions, ReadOnlyParser.DefaultContext);
        _directParser = new DirectCompactParserSession(
            CompactMetadataOptions.ParentLinks,
            CompactBufferOwnership.Pooled,
            parserOptions: selectedOptions
        );

        var textOptions = CreateTextOptions();
        _readOnlyTextParser = new HtmlParser(textOptions, ReadOnlyParser.DefaultContext);
        _directTextParser = new DirectCompactParserSession(
            CompactMetadataOptions.ParentLinks,
            CompactBufferOwnership.Pooled,
            parserOptions: textOptions
        );
    }

    [GlobalSetup]
    public void ValidateWorkloads()
    {
        if (ReadOnlySelectedSubtreeQuery() != DirectSelectedSubtreeQuery())
            throw new InvalidOperationException("Selected-subtree query implementations disagree.");
        if (ReadOnlyAttributeFreeTextQuery() != DirectAttributeFreeTextQuery())
            throw new InvalidOperationException("Attribute-free text query implementations disagree.");
    }

    [Benchmark(Baseline = true)]
    public int ReadOnlySelectedSubtreeQuery()
    {
        var filter = new OnlyElementWithIdAndDescendants("section", TargetId);
        using var document = _readOnlyParser.ParseReadOnlyDocument(_targetPage.AsMemory(), filter.Loop);
        return ReadOnlyChecksum(document);
    }

    [Benchmark]
    public int DirectSelectedSubtreeQuery()
    {
        var filter = new OnlyElementWithIdAndDescendants("section", TargetId);
        using var document = _directParser.Parse(_targetPage.AsMemory(), filter.Loop);
        return CompactChecksum(document);
    }

    [Benchmark]
    public int ReadOnlyAttributeFreeTextQuery()
    {
        var filter = new FirstTagAndAllChildren("body");
        using var document = _readOnlyTextParser.ParseReadOnlyDocument(_textPage.AsMemory(), filter.Loop);
        return ReadOnlyChecksum(document);
    }

    [Benchmark]
    public int DirectAttributeFreeTextQuery()
    {
        var filter = new FirstTagAndAllChildren("body");
        using var document = _directTextParser.Parse(_textPage.AsMemory(), filter.Loop);
        return CompactChecksum(document);
    }

    private static int ReadOnlyChecksum(IReadOnlyNode node)
    {
        var checksum = node.NodeName.Length;
        if (node is IReadOnlyElement element)
        {
            checksum += element.Attributes.Length * 17;
            foreach (var attribute in element.Attributes)
                checksum += attribute.Name.Length + attribute.Value.Length;
        }
        else if (node is IReadOnlyTextNode text)
        {
            checksum += text.Content.Length;
        }

        var children = node is IReadOnlyTemplateElement template ? template.Content : node.ChildNodes;
        foreach (var child in children)
            checksum += ReadOnlyChecksum(child);
        return checksum;
    }

    private static int CompactChecksum(HotCompactDocument document)
    {
        var checksum = 0;
        for (var handle = 0; handle < document.NodeCount; handle++)
        {
            ref readonly var node = ref document.GetNode(handle);
            checksum += document.GetName(node.NameId).Length;
            if (node.PayloadIndex < 0)
                continue;
            ref readonly var payload = ref document.GetPayload(node.PayloadIndex);
            checksum += payload.AttributeCount * 17;
            checksum += payload.ValueLength;
            for (var index = 0; index < payload.AttributeCount; index++)
            {
                ref readonly var attribute = ref document.GetAttribute(payload.FirstAttribute + index);
                checksum += document.GetName(attribute.NameId).Length + attribute.ValueLength;
            }
        }
        return checksum;
    }

    private static HtmlParserOptions CreateSelectedOptions() =>
        new()
        {
            IsNotConsumingCharacterReferences = true,
            IsNotSupportingFrames = true,
            SkipScriptText = true,
            SkipRawText = true,
            SkipComments = true,
            SkipPlaintext = true,
            SkipCDATA = true,
            SkipRCDataText = true,
            SkipProcessingInstructions = true,
            DisableElementPositionTracking = true,
            ShouldEmitAttribute = static (ref StructHtmlToken token, ReadOnlyMemory<char> name) =>
                token.Name == "section" && name.Span is "id"
                || token.Name == "tr" && name.Span is "class" or "data-time",
        };

    private static HtmlParserOptions CreateTextOptions() =>
        new()
        {
            IsNotConsumingCharacterReferences = true,
            IsNotSupportingFrames = true,
            SkipScriptText = false,
            SkipRawText = true,
            SkipDataText = false,
            SkipComments = true,
            SkipPlaintext = true,
            SkipCDATA = true,
            SkipRCDataText = true,
            SkipProcessingInstructions = true,
            DisableElementPositionTracking = true,
            ShouldEmitAttribute = static (ref StructHtmlToken _, ReadOnlyMemory<char> _) => false,
        };

    private static string CreateTargetPage()
    {
        var html = new StringBuilder("<html><body><nav><a href='/ignored'>ignored</a></nav>");
        html.Append("<section id='").Append(TargetId).Append("'><table><tbody>");
        for (var index = 0; index < 80; index++)
        {
            html.Append("<tr class='event' data-time='")
                .Append(index)
                .Append("'><td>Status ")
                .Append(index)
                .Append("</td><td>Place ")
                .Append(index % 7)
                .Append("</td></tr>");
        }
        return html.Append("</tbody></table></section><footer>ignored</footer></body></html>").ToString();
    }

    private static string CreateTextPage()
    {
        var html = new StringBuilder("<html><head><style>ignored</style></head><body>");
        for (var index = 0; index < 120; index++)
            html.Append("<p data-unused='x'>Text row ").Append(index).Append("</p>");
        html.Append("<script>function parseResult(){return 'marker';}</script>");
        return html.Append("</body></html>").ToString();
    }
}
#endif
