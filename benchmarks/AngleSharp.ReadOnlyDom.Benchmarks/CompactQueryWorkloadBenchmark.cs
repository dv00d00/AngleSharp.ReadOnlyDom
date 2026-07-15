#if NET10_0
using System.Text;
using AngleSharp.Common;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.ReadOnlyDom.Compact;
using AngleSharp.ReadOnlyDom.Filters;
using AngleSharp.ReadOnlyDom.Html;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

[MemoryDiagnoser]
public class CompactQueryWorkloadBenchmark
{
    private const string TargetId = "selected-region";
    private readonly HtmlParser _readOnlyParser;
    private readonly HtmlParser _frozenParser;
    private readonly HtmlParser _packedParser;
    private readonly HtmlParser _readOnlyTextParser;
    private readonly HtmlParser _frozenTextParser;
    private readonly HtmlParser _packedTextParser;
    private readonly string _targetPage = CreateTargetPage();
    private readonly string _textPage = CreateTextPage();

    public CompactQueryWorkloadBenchmark()
    {
        var selectedOptions = CreateSelectedOptions();
        _readOnlyParser = new HtmlParser(selectedOptions, ReadOnlyParser.DefaultContext);
        _frozenParser = CompactParser.CreateParser(CompactMetadataOptions.ParentLinks, parserOptions: selectedOptions);
        _packedParser = CompactParser.CreateParser(
            CompactMetadataOptions.ParentLinks,
            parserOptions: selectedOptions,
            layout: CompactDocumentLayout.Packed
        );

        var textOptions = CreateTextOptions();
        _readOnlyTextParser = new HtmlParser(textOptions, ReadOnlyParser.DefaultContext);
        _frozenTextParser = CompactParser.CreateParser(CompactMetadataOptions.ParentLinks, parserOptions: textOptions);
        _packedTextParser = CompactParser.CreateParser(
            CompactMetadataOptions.ParentLinks,
            parserOptions: textOptions,
            layout: CompactDocumentLayout.Packed
        );
    }

    [GlobalSetup]
    public void ValidateWorkloads()
    {
        if (ReadOnlySelectedSubtreeQuery() != FrozenSelectedSubtreeQuery())
            throw new InvalidOperationException("Selected-subtree query implementations disagree.");
        if (ReadOnlySelectedSubtreeQuery() != PackedSelectedSubtreeQuery())
            throw new InvalidOperationException("Packed selected-subtree query implementations disagree.");
        if (ReadOnlyAttributeFreeTextQuery() != FrozenAttributeFreeTextQuery())
            throw new InvalidOperationException("Attribute-free text query implementations disagree.");
        if (ReadOnlyAttributeFreeTextQuery() != PackedAttributeFreeTextQuery())
            throw new InvalidOperationException("Packed attribute-free query implementations disagree.");
    }

    [Benchmark(Baseline = true)]
    public int ReadOnlySelectedSubtreeQuery()
    {
        var filter = new OnlyElementWithIdAndDescendants("section", TargetId);
        using var document = _readOnlyParser.ParseReadOnlyDocument(_targetPage.AsMemory(), filter.Loop);
        return ReadOnlyChecksum(document);
    }

    [Benchmark]
    public int FrozenSelectedSubtreeQuery()
    {
        var filter = new OnlyElementWithIdAndDescendants("section", TargetId);
        using var document = _frozenParser.ParseCompactDocument(_targetPage.AsMemory(), filter.Loop);
        return CompactChecksum(document);
    }

    [Benchmark]
    public int PackedSelectedSubtreeQuery()
    {
        var filter = new OnlyElementWithIdAndDescendants("section", TargetId);
        using var document = _packedParser.ParseCompactDocument(_targetPage.AsMemory(), filter.Loop);
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
    public int FrozenAttributeFreeTextQuery()
    {
        var filter = new FirstTagAndAllChildren("body");
        using var document = _frozenTextParser.ParseCompactDocument(_textPage.AsMemory(), filter.Loop);
        return CompactChecksum(document);
    }

    [Benchmark]
    public int PackedAttributeFreeTextQuery()
    {
        var filter = new FirstTagAndAllChildren("body");
        using var document = _packedTextParser.ParseCompactDocument(_textPage.AsMemory(), filter.Loop);
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

    private static int CompactChecksum(CompactDocument document)
    {
        var checksum = 0;
        for (var handle = 0; handle < document.NodeCount; handle++)
        {
            var node = document.GetNode(handle);
            checksum += document.GetName(node.NameId).Length;
            if (node.PayloadIndex < 0)
                continue;
            var payload = document.GetPayload(node.PayloadIndex);
            checksum += payload.AttributeCount * 17;
            checksum += payload.ValueLength;
            for (var index = 0; index < payload.AttributeCount; index++)
            {
                var attribute = document.GetAttribute(payload.FirstAttribute + index);
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
