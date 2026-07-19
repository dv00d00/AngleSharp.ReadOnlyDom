#if NET10_0
using System.Text;
using System.Text.Json;
using AngleSharp.Html.Parser;
using AngleSharp.ReadOnlyDom.Streaming;

namespace AngleSharp.Readonly.Tests;

public sealed class Html5LibTokenizerTests
{
    private const int MaximumReportedFailures = 30;

    [Test]
    public async Task OfficialTokenizerVectorsMatchAcrossSegmentBoundaries()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "TestData", "html5lib-tokenizer");
        var failures = new List<string>();
        var failuresByFixtureAndState = new Dictionary<string, int>(StringComparer.Ordinal);
        var failuresByDescription = new Dictionary<string, int>(StringComparer.Ordinal);
        var firstFailureByFixture = new Dictionary<string, string>(StringComparer.Ordinal);
        var failureCount = 0;
        var executed = 0;
        var skippedNonScalarInputs = 0;

        foreach (var file in Directory.EnumerateFiles(root, "*.test").OrderBy(Path.GetFileName))
        {
            if (Path.GetFileName(file).Equals("xmlViolation.test", StringComparison.Ordinal))
                continue;

            using var document = JsonDocument.Parse(File.ReadAllBytes(file));
            foreach (var test in document.RootElement.GetProperty("tests").EnumerateArray())
            {
                var description = test.GetProperty("description").GetString() ?? "unnamed";
                var doubleEscaped = test.TryGetProperty("doubleEscaped", out var escaped) && escaped.GetBoolean();
                var input = DecodeRequired(test.GetProperty("input").GetString() ?? String.Empty, doubleEscaped);
                if (ContainsUnpairedSurrogate(input))
                {
                    skippedNonScalarInputs++;
                    continue;
                }
                var lastStartTag = test.TryGetProperty("lastStartTag", out var lastTag)
                    ? Decode(lastTag.GetString() ?? String.Empty, doubleEscaped)
                    : null;
                var expected = ReadExpected(test.GetProperty("output"), doubleEscaped);
                var states = test.TryGetProperty("initialStates", out var initialStates)
                    ? initialStates.EnumerateArray().Select(static state => state.GetString()!).ToArray()
                    : ["Data state"];

                foreach (var state in states)
                {
                    var utf8 = Encoding.UTF8.GetBytes(input);
                    foreach (var segmentSize in new[] { Math.Max(utf8.Length, 1), 1, 7 }.Distinct())
                    {
                        executed++;
                        var actual = Tokenize(utf8, segmentSize, state, lastStartTag);
                        if (actual.SequenceEqual(expected, StringComparer.Ordinal))
                            continue;

                        failureCount++;
                        var failureGroup = $"{Path.GetFileName(file)} | {state}";
                        failuresByFixtureAndState.TryGetValue(failureGroup, out var failuresInGroup);
                        failuresByFixtureAndState[failureGroup] = failuresInGroup + 1;
                        var descriptionGroup = $"{Path.GetFileName(file)} | {description} | {state}";
                        failuresByDescription.TryGetValue(descriptionGroup, out var failuresForDescription);
                        failuresByDescription[descriptionGroup] = failuresForDescription + 1;
                        var failure =
                            $"{Path.GetFileName(file)} | {description} | {state} | segment={segmentSize}\n"
                            + $"  expected: {Format(expected)}\n"
                            + $"  actual:   {Format(actual)}";
                        firstFailureByFixture.TryAdd(Path.GetFileName(file), failure);
                        if (failures.Count < MaximumReportedFailures)
                        {
                            failures.Add(failure);
                        }
                    }
                }
            }
        }

        await Assert.That(executed).IsEqualTo(19_716);
        await Assert.That(skippedNonScalarInputs).IsEqualTo(4);
        await Assert
            .That(failureCount)
            .IsEqualTo(0)
            .Because(
                $"Executed {executed:N0} cases; skipped {skippedNonScalarInputs:N0} inputs not representable as UTF-8.\n"
                    + "Failure groups:\n"
                    + String.Join(
                        "\n",
                        failuresByFixtureAndState
                            .OrderByDescending(static pair => pair.Value)
                            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
                            .Select(static pair => $"  {pair.Value,4}  {pair.Key}")
                    )
                    + "\nTop failing vectors:\n"
                    + String.Join(
                        "\n",
                        failuresByDescription
                            .OrderByDescending(static pair => pair.Value)
                            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
                            .Take(40)
                            .Select(static pair => $"  {pair.Value,4}  {pair.Key}")
                    )
                    + "\nFirst mismatch per failing fixture:\n"
                    + String.Join("\n", firstFailureByFixture.Values)
                    + "\nFirst mismatches:\n"
                    + String.Join("\n", failures)
            );
    }

    private static IReadOnlyList<string> Tokenize(
        byte[] utf8,
        int segmentSize,
        string initialState,
        string? lastStartTag
    )
    {
        var sink = new SpecSink();
        var tokenizer = new Utf8HtmlTokenizer(sink);
        switch (initialState)
        {
            case "Data state":
                break;
            case "PLAINTEXT state":
                tokenizer.SetMode(HtmlParseMode.Plaintext, lastStartTag);
                break;
            case "RCDATA state":
                tokenizer.SetMode(HtmlParseMode.RCData, lastStartTag);
                break;
            case "RAWTEXT state":
                tokenizer.SetMode(HtmlParseMode.Rawtext, lastStartTag);
                break;
            case "Script data state":
                tokenizer.SetMode(HtmlParseMode.Script, lastStartTag);
                break;
            case "CDATA section state":
                tokenizer.EnterCDataSection();
                break;
            default:
                throw new NotSupportedException($"Unsupported tokenizer state: {initialState}");
        }

        for (var offset = 0; offset < utf8.Length; offset += segmentSize)
            tokenizer.Write(utf8.AsMemory(offset, Math.Min(segmentSize, utf8.Length - offset)));
        tokenizer.Complete();
        return sink.Tokens.Select(static token => token.Canonical()).ToArray();
    }

    private static IReadOnlyList<string> ReadExpected(JsonElement output, bool doubleEscaped)
    {
        var tokens = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            var values = item.EnumerateArray().ToArray();
            var kind = values[0].GetString();
            SpecToken token;
            switch (kind)
            {
                case "DOCTYPE":
                    token = SpecToken.Doctype(
                        Decode(NullableString(values[1]), doubleEscaped),
                        Decode(NullableString(values[2]), doubleEscaped),
                        values[2].ValueKind == JsonValueKind.Null,
                        Decode(NullableString(values[3]), doubleEscaped),
                        values[3].ValueKind == JsonValueKind.Null,
                        quirks: !values[4].GetBoolean()
                    );
                    break;
                case "StartTag":
                    var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var attribute in values[2].EnumerateObject())
                    {
                        attributes.Add(
                            DecodeRequired(attribute.Name, doubleEscaped),
                            DecodeRequired(attribute.Value.GetString() ?? String.Empty, doubleEscaped)
                        );
                    }
                    token = SpecToken.StartTag(
                        DecodeRequired(values[1].GetString() ?? String.Empty, doubleEscaped),
                        attributes,
                        values.Length > 3 && values[3].GetBoolean()
                    );
                    break;
                case "EndTag":
                    token = SpecToken.EndTag(DecodeRequired(values[1].GetString() ?? String.Empty, doubleEscaped));
                    break;
                case "Comment":
                    token = SpecToken.Comment(DecodeRequired(values[1].GetString() ?? String.Empty, doubleEscaped));
                    break;
                case "Character":
                    token = SpecToken.Text(DecodeRequired(values[1].GetString() ?? String.Empty, doubleEscaped));
                    break;
                default:
                    throw new InvalidDataException($"Unknown html5lib token kind: {kind}");
            }

            if (
                token.Kind == "Character"
                && tokens.Count != 0
                && tokens[^1].StartsWith("Character:", StringComparison.Ordinal)
            )
            {
                var previous = JsonSerializer.Deserialize<string>(tokens[^1]["Character:".Length..])!;
                tokens[^1] = SpecToken.Text(previous + token.Data).Canonical();
            }
            else
            {
                tokens.Add(token.Canonical());
            }
        }
        return tokens;
    }

    private static string? NullableString(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : value.GetString();

    private static string? Decode(string? value, bool doubleEscaped)
    {
        if (value is null || !doubleEscaped || !value.Contains("\\u", StringComparison.Ordinal))
            return value;

        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (
                value[index] == '\\'
                && index + 5 < value.Length
                && value[index + 1] == 'u'
                && UInt16.TryParse(
                    value.AsSpan(index + 2, 4),
                    System.Globalization.NumberStyles.HexNumber,
                    null,
                    out var scalar
                )
            )
            {
                result.Append((char)scalar);
                index += 5;
            }
            else
            {
                result.Append(value[index]);
            }
        }
        return result.ToString();
    }

    private static string DecodeRequired(string value, bool doubleEscaped) => Decode(value, doubleEscaped)!;

    private static bool ContainsUnpairedSurrogate(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (Char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !Char.IsLowSurrogate(value[index + 1]))
                    return true;
                index++;
            }
            else if (Char.IsLowSurrogate(value[index]))
            {
                return true;
            }
        }
        return false;
    }

    private static string Format(IReadOnlyList<string> tokens) =>
        "[" + String.Join(", ", tokens.Take(12)) + (tokens.Count > 12 ? ", …" : String.Empty) + "]";

    private sealed class SpecSink : IUtf8HtmlTokenSink
    {
        private readonly List<SpecToken> _tokens = [];
        private SpecToken? _startTag;

        public IReadOnlyList<SpecToken> Tokens => _tokens;

        public void Text(ReadOnlySpan<byte> utf8)
        {
            var value = Encoding.UTF8.GetString(utf8);
            if (_tokens.Count != 0 && _tokens[^1].Kind == "Character")
                _tokens[^1].Data += value;
            else
                _tokens.Add(SpecToken.Text(value));
        }

        public Utf8HtmlStartTagCapture StartTag(Utf8HtmlName name)
        {
            _startTag = SpecToken.StartTag(
                DecodeSemanticName(name),
                new Dictionary<string, string>(),
                false
            );
            return Utf8HtmlStartTagCapture.Attributes;
        }

        public bool WantsAttribute(Utf8HtmlName name) => true;

        public void Attribute(Utf8HtmlName name, ReadOnlySpan<byte> value) =>
            _startTag!.Attributes!.Add(DecodeSemanticName(name), Encoding.UTF8.GetString(value));

        public void StartTagEnd(bool selfClosing)
        {
            _startTag!.SelfClosing = selfClosing;
            _tokens.Add(_startTag);
            _startTag = null;
        }

        public void EndTag(Utf8HtmlName name) => _tokens.Add(SpecToken.EndTag(DecodeSemanticName(name)));

        private static string DecodeSemanticName(Utf8HtmlName name)
        {
            var bytes = name.Verbatim.ToArray();
            for (var index = 0; index < bytes.Length; index++)
            {
                var value = bytes[index];
                if ((uint)(value - (byte)'A') <= 'Z' - 'A')
                    bytes[index] = (byte)(value | 0x20);
            }
            return Encoding.UTF8.GetString(bytes);
        }

        public void Comment(ReadOnlySpan<byte> utf8) => _tokens.Add(SpecToken.Comment(Encoding.UTF8.GetString(utf8)));

        public void Doctype(in Utf8DoctypeToken token) =>
            _tokens.Add(
                SpecToken.Doctype(
                    Encoding.UTF8.GetString(token.Name),
                    Encoding.UTF8.GetString(token.PublicIdentifier),
                    token.IsPublicIdentifierMissing,
                    Encoding.UTF8.GetString(token.SystemIdentifier),
                    token.IsSystemIdentifierMissing,
                    token.IsQuirksForced
                )
            );
    }

    private sealed class SpecToken
    {
        private SpecToken(string kind) => Kind = kind;

        public string Kind { get; }
        public string? Name { get; init; }
        public string? Data { get; set; }
        public Dictionary<string, string>? Attributes { get; init; }
        public bool SelfClosing { get; set; }
        public string? PublicIdentifier { get; init; }
        public bool PublicIdentifierMissing { get; init; }
        public string? SystemIdentifier { get; init; }
        public bool SystemIdentifierMissing { get; init; }
        public bool Quirks { get; init; }

        public static SpecToken Text(string value) => new("Character") { Data = value };

        public static SpecToken Comment(string value) => new("Comment") { Data = value };

        public static SpecToken EndTag(string name) => new("EndTag") { Name = name };

        public static SpecToken StartTag(string name, Dictionary<string, string> attributes, bool selfClosing) =>
            new("StartTag")
            {
                Name = name,
                Attributes = attributes,
                SelfClosing = selfClosing,
            };

        public static SpecToken Doctype(
            string? name,
            string? publicIdentifier,
            bool publicIdentifierMissing,
            string? systemIdentifier,
            bool systemIdentifierMissing,
            bool quirks
        ) =>
            new("DOCTYPE")
            {
                Name = name ?? String.Empty,
                PublicIdentifier = publicIdentifier ?? String.Empty,
                PublicIdentifierMissing = publicIdentifierMissing,
                SystemIdentifier = systemIdentifier ?? String.Empty,
                SystemIdentifierMissing = systemIdentifierMissing,
                Quirks = quirks,
            };

        public string Canonical() =>
            Kind switch
            {
                "Character" or "Comment" => $"{Kind}:{JsonSerializer.Serialize(Data)}",
                "EndTag" => $"EndTag:{JsonSerializer.Serialize(Name)}",
                "StartTag" => $"StartTag:{JsonSerializer.Serialize(Name)}:{SelfClosing.ToString().ToLowerInvariant()}:"
                    + String.Join(
                        ",",
                        Attributes!
                            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                            .Select(static pair =>
                                $"{JsonSerializer.Serialize(pair.Key)}={JsonSerializer.Serialize(pair.Value)}"
                            )
                    ),
                "DOCTYPE" =>
                    $"DOCTYPE:{JsonSerializer.Serialize(Name)}:{PublicIdentifierMissing.ToString().ToLowerInvariant()}:"
                        + $"{JsonSerializer.Serialize(PublicIdentifier)}:{SystemIdentifierMissing.ToString().ToLowerInvariant()}:"
                        + $"{JsonSerializer.Serialize(SystemIdentifier)}:{Quirks.ToString().ToLowerInvariant()}",
                _ => throw new InvalidOperationException($"Unknown token kind: {Kind}"),
            };
    }
}
#endif
