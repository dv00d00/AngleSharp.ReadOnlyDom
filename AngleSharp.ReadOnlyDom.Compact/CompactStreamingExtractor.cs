using System.Runtime.InteropServices;
using System.Text;
using AngleSharp.Common;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.ReadOnlyDom.Compact.Arena;
using AngleSharp.Text;
using ConstructionArena = AngleSharp.ReadOnlyDom.Compact.Arena.Arena;

namespace AngleSharp.ReadOnlyDom.Compact;

public readonly record struct CompactStreamingExtractionCounters(
    int TokensProcessed,
    int NodesMaterialized,
    int AttributesInspected,
    int AttributesRetained,
    int TextValuesRetained,
    int ValuesDecoded,
    int InputBytesConsumed,
    bool EarlyTerminated
);

public sealed class CompactStreamingExtractionResult
{
    internal CompactStreamingExtractionResult(
        CompactExtractionValue value,
        CompactStreamingExtractionCounters counters
    )
    {
        Value = value;
        Counters = counters;
    }

    public bool Found => Value.Exists;
    public CompactExtractionValue Value { get; }
    public CompactStreamingExtractionCounters Counters { get; }
    public string ExecutionMode => "query-directed-construction";
}

/// <summary>
/// Runs the concrete construction-time view used by issue #17: first HTML element matching tag and ID to owned,
/// normalized descendant text. Unsupported result shapes should use <see cref="CompactExtractionPlan"/> over a
/// materialized compact document.
/// </summary>
public static class CompactStreamingExtractor
{
    public static CompactStreamingExtractionPlan CompileFirstNormalizedText(
        string tag = "div",
        string id = "content"
    ) => new(tag, id);

    public static CompactStreamingExtractionResult ExtractFirstNormalizedText(
        string source,
        string tag = "div",
        string id = "content"
    ) => CompileFirstNormalizedText(tag, id).Execute(source);
}

public sealed class CompactStreamingExtractionPlan
{
    private readonly IBrowsingContext _context;
    private readonly HtmlParserOptions _parserOptions;

    internal CompactStreamingExtractionPlan(string tag, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var factory = new ArenaConstructionFactory(
            new CompactParserHints(),
            trackSourceReferences: false,
            CompactMetadataOptions.None,
            CompactDocumentLayout.FrozenColumns,
            new CompactStreamingExtractionDefinition(tag, id)
        );
        _context = BrowsingContext.New(Configuration.Default.With(_ => factory));
        _parserOptions = CompactParser.CreateParserOptions(CompactMetadataOptions.None);
        _parserOptions.ShouldEmitAttribute = ShouldRetainAttribute;
    }

    public CompactStreamingExtractionResult Execute(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var parser = new HtmlParser(_parserOptions, _context);
        var textSource = new TextSource(new StringTextSource(source));
        var tokensProcessed = 0;
        TokenizerMiddleware middleware = (ref StructHtmlToken token, TokenConsumer next) =>
        {
            tokensProcessed++;
            next(ref token);
            return TokenConsumptionResult.Continue;
        };
        var document = parser.ParseDocument<ArenaDocument, ArenaElement>(textSource, middleware);
        try
        {
            document.SetTokensProcessed(tokensProcessed);
            var consumed = Math.Min(source.Length, document.Source.Index);
            return document.CreateStreamingExtractionResult(Encoding.UTF8.GetByteCount(source.AsSpan(0, consumed)));
        }
        finally
        {
            document.Dispose();
        }
    }

    private static bool ShouldRetainAttribute(ref StructHtmlToken token, ReadOnlyMemory<char> name)
    {
        // Keep attributes read by HtmlDomBuilder itself. The requested ID is also the only query
        // predicate in this concrete view. Attributes copied by obsolete isindex handling do not
        // affect topology or the projected text, so they can remain filtered.
        var attribute = name.Span;
        if (
            attribute.Equals("id", StringComparison.OrdinalIgnoreCase)
            || attribute.Equals("type", StringComparison.OrdinalIgnoreCase)
            || attribute.Equals("action", StringComparison.OrdinalIgnoreCase)
            || attribute.Equals("prompt", StringComparison.OrdinalIgnoreCase)
            || attribute.Equals("encoding", StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        var tag = token.Name.Memory.Span;
        // The adoption agency and reconstruction algorithms compare the complete attribute sets
        // of active formatting elements. Dropping any attribute on these tags can change topology.
        return tag.Equals("a", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("b", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("big", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("code", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("em", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("font", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("i", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("nobr", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("s", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("small", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("strike", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("strong", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("tt", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("u", StringComparison.OrdinalIgnoreCase);
    }

}

internal readonly record struct CompactStreamingExtractionDefinition(string Tag, string Id)
{
    public CompactStreamingExtractionState CreateState(string source) => new(source, Tag, Id);
}

internal sealed class CompactStreamingExtractionState
{
    private readonly string _source;
    private readonly string _tag;
    private readonly string _id;
    private readonly List<int> _candidates = [];
    private int _tokensProcessed;
    private int _nodesMaterialized;
    private int _attributesInspected;
    private int _attributesRetained;
    private int _textValuesRetained;
    private int _valuesDecoded;

    public CompactStreamingExtractionState(string source, string tag, string id)
    {
        _source = source;
        _tag = tag;
        _id = id;
    }

    public void SetTokensProcessed(int count) => _tokensProcessed = count;

    public void NodeMaterialized() => _nodesMaterialized++;

    public void AttributeRetained(StringOrMemory value)
    {
        _attributesRetained++;
        ObserveDecoded(value);
    }

    public void CompleteAttributes(ConstructionArena arena, int handle)
    {
        if (!arena.LocalName(handle).Memory.Span.Equals(_tag, StringComparison.OrdinalIgnoreCase))
            return;

        for (
            var attribute = arena.FirstAttributeHandle(handle);
            attribute >= 0;
            attribute = arena.NextAttribute(attribute)
        )
        {
            _attributesInspected++;
            if (!arena.AttributeName(attribute).Memory.Span.Equals("id", StringComparison.OrdinalIgnoreCase))
                continue;
            if (arena.AttributeValue(attribute).Memory.Span.SequenceEqual(_id))
                _candidates.Add(handle);
            break;
        }
    }

    public StringOrMemory SelectTextValue(StringOrMemory value)
    {
        ObserveDecoded(value);
        if (_candidates.Count == 0)
            return default;
        _textValuesRetained++;
        return value;
    }

    public CompactStreamingExtractionResult CreateResult(
        ConstructionArena arena,
        int root,
        int inputBytesConsumed
    )
    {
        var target = FindFirstCandidate(arena, root);
        var value = target < 0 ? default : new CompactExtractionValue(NormalizeText(arena, target));
        return new CompactStreamingExtractionResult(
            value,
            new CompactStreamingExtractionCounters(
                _tokensProcessed,
                _nodesMaterialized,
                _attributesInspected,
                _attributesRetained,
                _textValuesRetained,
                _valuesDecoded,
                inputBytesConsumed,
                EarlyTerminated: false
            )
        );
    }

    private int FindFirstCandidate(ConstructionArena arena, int handle)
    {
        if (_candidates.Contains(handle))
            return handle;
        for (var child = arena.FirstChild(handle); child >= 0; child = arena.NextSibling(child))
        {
            var match = FindFirstCandidate(arena, child);
            if (match >= 0)
                return match;
        }
        return -1;
    }

    private static string NormalizeText(ConstructionArena arena, int target)
    {
        var output = new StringBuilder();
        var pendingSpace = false;
        Append(target);
        return output.ToString();

        void Append(int handle)
        {
            if (arena.Kind(handle) == CompactNodeKind.Text)
            {
                foreach (var character in arena.Value(handle).Memory.Span)
                {
                    if (char.IsWhiteSpace(character))
                    {
                        pendingSpace = output.Length != 0;
                        continue;
                    }
                    if (pendingSpace)
                    {
                        output.Append(' ');
                        pendingSpace = false;
                    }
                    output.Append(character);
                }
            }

            for (var child = arena.FirstChild(handle); child >= 0; child = arena.NextSibling(child))
                Append(child);
        }
    }

    private void ObserveDecoded(StringOrMemory value)
    {
        if (!MemoryMarshal.TryGetString(value.Memory, out var backing, out _, out _) || !ReferenceEquals(backing, _source))
        {
            _valuesDecoded++;
        }
    }
}
