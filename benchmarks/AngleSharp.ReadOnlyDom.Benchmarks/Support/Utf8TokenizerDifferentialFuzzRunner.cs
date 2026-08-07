#if NET10_0
using System.Buffers;
using System.Text;
using AngleSharp.Html;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens;
using AngleSharp.ReadOnlyDom.Streaming.Tokenization;
using AngleSharp.Text;

namespace AngleSharp.ReadOnlyDom.Benchmarks.Support;

// Coverage-guided mutational differential fuzzer for the streaming tokenizer.
//
// The AngleSharp tokenizer is the semantic oracle. Each candidate is also replayed through this
// tokenizer with hostile input segmentation, which gives us a second independent property:
// tokenization must not depend on PipeReader / socket chunk boundaries.
//
//   dotnet run --project benchmarks/AngleSharp.ReadOnlyDom.Benchmarks -c Release -f net10.0 -- \
//     --utf8-tokenizer-fuzz --iterations 10000 --seed 12345
//
// A failure is minimized before being written to artifacts/fuzz. Pass the emitted .html file to
// --repro for a deterministic single-case replay.
internal static class Utf8TokenizerDifferentialFuzzRunner
{
    private const int DefaultIterations = 5_000;
    private const int DefaultSeed = 12_345;
    private const int MaximumCandidateBytes = 4_096;
    private const int MaximumCorpusEntries = 2_048;

    private static readonly byte[][] Seeds =
    [
        "plain text"u8.ToArray(),
        "<div class=a data-x='b'>text&amp;tail</div>"u8.ToArray(),
        "<!--comment--><p>x</p>"u8.ToArray(),
        "<!doctype html><title>a&amp;b</title>"u8.ToArray(),
        "<textarea>a&amp;b</textarea><style>a>b{color:red}</style>"u8.ToArray(),
        "<script>if (a < b) x = '</not-script>'; </script><p>end</p>"u8.ToArray(),
        "<script><!--<script>double escaped</script>still script--></script>"u8.ToArray(),
        "<plaintext>a</plaintext><b>still text</b>"u8.ToArray(),
        "<a x='&notit;' y=/a&#x3D;1&amp;b=2 disabled>x</a>"u8.ToArray(),
        "<!DOCTYPE html PUBLIC '-//W3C//DTD HTML 4.01//EN' 'about:legacy-compat'>"u8.ToArray(),
        "<svg><![CDATA[a<b]]><foreignObject><p>x</p></foreignObject></svg>"u8.ToArray(),
        "<table><p>misnested</table>tail"u8.ToArray(),
        "<x-custom data-a==b data-b=unterminated"u8.ToArray(),
        "&amp;&notin;&#0;&#xD800;&#999999999999999999999;"u8.ToArray(),
        "é漢🙂\r\n\0tail<"u8.ToArray(),
    ];

    private static readonly byte[][] InterestingFragments =
    [
        "<"u8.ToArray(),
        ">"u8.ToArray(),
        "</"u8.ToArray(),
        "<!"u8.ToArray(),
        "<!--"u8.ToArray(),
        "-->"u8.ToArray(),
        "<![CDATA["u8.ToArray(),
        "]]>"u8.ToArray(),
        "&"u8.ToArray(),
        ";"u8.ToArray(),
        "&#"u8.ToArray(),
        "&#x"u8.ToArray(),
        "="u8.ToArray(),
        "'"u8.ToArray(),
        "\""u8.ToArray(),
        "\0"u8.ToArray(),
        "\r"u8.ToArray(),
        "\n"u8.ToArray(),
        "<script>"u8.ToArray(),
        "</script>"u8.ToArray(),
        "<style>"u8.ToArray(),
        "</style>"u8.ToArray(),
        "<textarea>"u8.ToArray(),
        "</textarea>"u8.ToArray(),
        "<plaintext>"u8.ToArray(),
        "<div a='b' c=d>"u8.ToArray(),
        "</div>"u8.ToArray(),
        "é"u8.ToArray(),
        "中"u8.ToArray(),
        "😀"u8.ToArray(),
    ];

    public static int Run(string[] args)
    {
        var options = Options.Parse(args);
        Directory.CreateDirectory(options.OutputDirectory);

        if (options.ReproPath is not null)
            return Repro(options);

        var random = new Random(options.Seed);
        var corpus = Seeds.Select(static seed => seed.ToArray()).ToList();
        LoadCorpus(options.CorpusPath, corpus);
        var coverage = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seed in corpus.ToArray())
            coverage.Add(TokenizeOurs(seed, ChunkLayout.Contiguous, 0).Coverage);

        var uniqueFailures = new HashSet<string>(StringComparer.Ordinal);
        var minimizedFailures = new HashSet<string>(StringComparer.Ordinal);
        var failureCount = 0;
        Console.WriteLine(
            $"UTF-8 tokenizer differential fuzz: iterations={options.Iterations} seed={options.Seed} corpus={corpus.Count}"
        );

        for (var iteration = 0; iteration < options.Iterations; iteration++)
        {
            var parent = corpus[random.Next(corpus.Count)];
            var candidate = Mutate(parent, corpus, random);
            var outcome = Evaluate(candidate, random.Next());

            if (coverage.Add(outcome.Coverage) && corpus.Count < MaximumCorpusEntries)
                corpus.Add(candidate);

            if (outcome.Failure is not { } failure)
                continue;

            var signature = failure.Signature;
            if (!uniqueFailures.Add(signature))
                continue;

            var minimized = Minimize(candidate, failure, options.MinimizeChecks);
            var minimizedFailure = EvaluateForFailure(minimized, failure) ?? failure;
            if (!minimizedFailures.Add(minimizedFailure.Signature))
                continue;

            failureCount++;
            WriteFailure(options, iteration, candidate, minimized, minimizedFailure);

            Console.WriteLine(
                $"FOUND {minimizedFailure.Kind} iter={iteration} original={candidate.Length}B minimized={minimized.Length}B"
            );
            Console.WriteLine($"  input: {Escape(minimized)}");
            Console.WriteLine($"  {minimizedFailure.DescribeDifference()}");

            if (failureCount >= options.MaximumFailures)
                break;
        }

        Console.WriteLine(
            failureCount == 0
                ? $"OK: {options.Iterations} mutations, {coverage.Count} coverage shapes, no divergence."
                : $"FAILED: {failureCount} unique divergence(s); reproducers are in {options.OutputDirectory}."
        );
        return failureCount == 0 ? 0 : 1;
    }

    private static int Repro(Options options)
    {
        var input = File.ReadAllBytes(options.ReproPath!);
        var outcome = Evaluate(input, options.Seed);
        Console.WriteLine($"input ({input.Length}B): {Escape(input)}");
        if (outcome.Failure is not { } failure)
        {
            Console.WriteLine("OK: no divergence.");
            return 0;
        }

        Console.WriteLine($"FOUND {failure.Kind} layout={failure.Layout}");
        Console.WriteLine(failure.DescribeDifference());
        Console.WriteLine("AngleSharp:");
        PrintTokens(failure.Expected);
        Console.WriteLine("ReadOnlyDom:");
        PrintTokens(failure.Actual);
        return 1;
    }

    private static FuzzOutcome Evaluate(byte[] input, int chunkSeed)
    {
        var contiguous = TokenizeOurs(input, ChunkLayout.Contiguous, chunkSeed);
        if (CanUseAngleSharpOracle(input))
        {
            var expected = TokenizeAngleSharp(input);
            if (
                !TokensEqual(expected, contiguous.Tokens)
                && !IsKnownAngleSharpOracleDifference(input, expected, contiguous.Tokens)
            )
            {
                return new FuzzOutcome(
                    contiguous.Coverage,
                    Failure.Create(
                        FailureKind.Differential,
                        ChunkLayout.Contiguous,
                        chunkSeed,
                        expected,
                        contiguous.Tokens
                    )
                );
            }
        }

        foreach (var layout in ChunkLayout.HostileLayouts)
        {
            var chunked = TokenizeOurs(input, layout, chunkSeed);
            if (!TokensEqual(contiguous.Tokens, chunked.Tokens))
            {
                return new FuzzOutcome(
                    contiguous.Coverage,
                    Failure.Create(FailureKind.Chunking, layout, chunkSeed, contiguous.Tokens, chunked.Tokens)
                );
            }
        }

        return new FuzzOutcome(contiguous.Coverage, null);
    }

    // These are documented AngleSharp tokenizer-oracle boundaries. html5lib expects a literal
    // U+0000 character token, while AngleSharp's low-level token API exposes an empty character
    // token. AngleSharp also drops the semicolon from a bare "&;", where html5lib retains it.
    // The candidates still exercise every chunking property; only the differential check is
    // skipped so known oracle behavior cannot hide useful findings behind repeated false alarms.
    private static bool CanUseAngleSharpOracle(ReadOnlySpan<byte> input) =>
        !input.Contains((byte)0) && input.IndexOf("&;"u8) < 0;

    private static bool IsKnownAngleSharpOracleDifference(
        ReadOnlySpan<byte> input,
        IReadOnlyList<Token> expected,
        IReadOnlyList<Token> actual
    )
    {
        var html = Encoding.UTF8.GetString(input);
        if (IsEscapedScriptEndTagOracleDifference(html, expected, actual))
            return true;
        if (expected.Count != actual.Count)
            return false;

        var difference = -1;
        for (var index = 0; index < expected.Count; index++)
        {
            if (expected[index] == actual[index])
                continue;
            if (difference >= 0)
                return false;
            difference = index;
        }
        if (difference < 0 || expected[difference].Kind != "text" || actual[difference].Kind != "text")
            return false;

        var expectedText = expected[difference].Value;
        var actualText = actual[difference].Value;

        // AngleSharp lowercases the temporary end-tag buffer at EOF. The HTML algorithm keeps
        // the original input bytes when an RCDATA/RAWTEXT candidate does not become a token.
        if (
            expectedText.Equals(actualText, StringComparison.OrdinalIgnoreCase) && HasUppercaseIncompleteRawEndTag(html)
        )
        {
            return true;
        }

        // In double-escaped script data every byte of the apparent </script... sequence has
        // already been emitted while deciding whether to leave double-escaped mode. AngleSharp's
        // low-level tokenizer drops that suffix at EOF; html5lib double-escaped vectors retain it.
        if (
            actualText.StartsWith(expectedText, StringComparison.Ordinal)
            && actualText.AsSpan(expectedText.Length).StartsWith("</script", StringComparison.OrdinalIgnoreCase)
            && IsDoubleEscapedScriptInput(html)
        )
        {
            return true;
        }

        return false;
    }

    private static bool IsEscapedScriptEndTagOracleDifference(
        string html,
        IReadOnlyList<Token> expected,
        IReadOnlyList<Token> actual
    )
    {
        if (!html.Contains("<!--", StringComparison.Ordinal))
            return false;
        var shared = Math.Min(expected.Count, actual.Count);
        for (var index = 0; index < shared; index++)
        {
            if (
                expected[index].Kind != "text"
                || actual[index].Kind != "text"
                || actual[index].Value.Length <= expected[index].Value.Length
                || !actual[index].Value.StartsWith(expected[index].Value, StringComparison.Ordinal)
            )
            {
                if (expected[index] != actual[index])
                    return false;
                continue;
            }

            var candidateStart = actual[index].Value.LastIndexOf("</", StringComparison.Ordinal);
            if (candidateStart < 0)
                return false;
            var nameStart = candidateStart + 2;
            var nameEnd = nameStart;
            while (nameEnd < actual[index].Value.Length)
            {
                var value = actual[index].Value[nameEnd];
                if (value is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z'))
                    break;
                nameEnd++;
            }
            if (nameEnd == nameStart)
                return false;
            var name = actual[index].Value[nameStart..nameEnd];
            return !name.Equals("script", StringComparison.OrdinalIgnoreCase) || IsDoubleEscapedScriptInput(html);
        }
        return false;
    }

    private static bool HasUppercaseIncompleteRawEndTag(string html)
    {
        return new[] { "title", "textarea", "style", "xmp", "iframe", "noembed", "noframes" }.Any(name =>
            html.Contains($"<{name}", StringComparison.OrdinalIgnoreCase)
        );
    }

    private static bool IsDoubleEscapedScriptInput(string html)
    {
        var script = html.IndexOf("<script", StringComparison.OrdinalIgnoreCase);
        if (script < 0)
            return false;
        var escaped = html.IndexOf("<!--", script, StringComparison.Ordinal);
        if (escaped < 0)
            return false;
        return html.IndexOf("<script", escaped + 4, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static Failure? EvaluateForFailure(byte[] input, Failure target)
    {
        if (target.Kind == FailureKind.Differential)
        {
            if (!CanUseAngleSharpOracle(input))
                return null;
            var expected = TokenizeAngleSharp(input);
            var actual = TokenizeOurs(input, ChunkLayout.Contiguous, target.ChunkSeed).Tokens;
            return TokensEqual(expected, actual) || IsKnownAngleSharpOracleDifference(input, expected, actual)
                ? null
                : Failure.Create(target.Kind, target.Layout, target.ChunkSeed, expected, actual);
        }

        var contiguous = TokenizeOurs(input, ChunkLayout.Contiguous, target.ChunkSeed).Tokens;
        var chunked = TokenizeOurs(input, target.Layout, target.ChunkSeed).Tokens;
        return TokensEqual(contiguous, chunked)
            ? null
            : Failure.Create(target.Kind, target.Layout, target.ChunkSeed, contiguous, chunked);
    }

    private static byte[] Minimize(byte[] original, Failure failure, int maximumChecks)
    {
        var current = original;
        var checks = 0;
        var granularity = 2;

        while (current.Length > 1 && checks < maximumChecks)
        {
            var chunkLength = (current.Length + granularity - 1) / granularity;
            var reduced = false;
            for (var start = 0; start < current.Length && checks < maximumChecks; start += chunkLength)
            {
                var length = Math.Min(chunkLength, current.Length - start);
                var candidate = RemoveRange(current, start, length);
                checks++;
                if (candidate.Length == 0 || EvaluateForFailure(candidate, failure) is null)
                    continue;

                current = candidate;
                granularity = Math.Max(2, granularity - 1);
                reduced = true;
                break;
            }

            if (reduced)
                continue;
            if (granularity >= current.Length)
                break;
            granularity = Math.Min(current.Length, granularity * 2);
        }

        ReadOnlySpan<byte> simplifications = "a0 <>/!-=&;'\"\r\n"u8;
        for (var index = 0; index < current.Length && checks < maximumChecks; index++)
        {
            foreach (var replacement in simplifications)
            {
                if (replacement == current[index])
                    continue;
                var candidate = current.ToArray();
                candidate[index] = replacement;
                checks++;
                if (EvaluateForFailure(candidate, failure) is not null)
                {
                    current = candidate;
                    break;
                }
                if (checks >= maximumChecks)
                    break;
            }
        }
        return current;
    }

    private static byte[] Mutate(byte[] parent, IReadOnlyList<byte[]> corpus, Random random)
    {
        var value = parent.ToList();
        var mutationCount = random.Next(1, 9);
        for (var mutation = 0; mutation < mutationCount; mutation++)
        {
            switch (random.Next(8))
            {
                case 0 when value.Count != 0:
                {
                    var start = random.Next(value.Count);
                    var length = random.Next(1, Math.Min(64, value.Count - start) + 1);
                    value.RemoveRange(start, length);
                    break;
                }
                case 1:
                    Insert(
                        value,
                        random.Next(value.Count + 1),
                        InterestingFragments[random.Next(InterestingFragments.Length)]
                    );
                    break;
                case 2 when value.Count != 0:
                    value[random.Next(value.Count)] = (byte)random.Next(128);
                    break;
                case 3 when value.Count != 0:
                {
                    var start = random.Next(value.Count);
                    var length = random.Next(1, Math.Min(64, value.Count - start) + 1);
                    Insert(value, random.Next(value.Count + 1), value.GetRange(start, length));
                    break;
                }
                case 4 when value.Count != 0:
                {
                    var start = random.Next(value.Count);
                    value.RemoveRange(start, value.Count - start);
                    break;
                }
                case 5 when value.Count != 0:
                {
                    var index = random.Next(value.Count);
                    var b = value[index];
                    value[index] = (byte)(b is >= (byte)'a' and <= (byte)'z' ? b - 32 : b | 0x20);
                    break;
                }
                case 6:
                {
                    var donor = corpus[random.Next(corpus.Count)];
                    if (donor.Length == 0)
                        break;
                    var start = random.Next(donor.Length);
                    var length = random.Next(1, Math.Min(128, donor.Length - start) + 1);
                    Insert(value, random.Next(value.Count + 1), donor.AsSpan(start, length));
                    break;
                }
                default:
                    Insert(value, random.Next(value.Count + 1), [(byte)random.Next(256)]);
                    break;
            }

            if (value.Count > MaximumCandidateBytes)
                value.RemoveRange(MaximumCandidateBytes, value.Count - MaximumCandidateBytes);
        }
        return [.. value];
    }

    private static void Insert(List<byte> target, int index, ReadOnlySpan<byte> value)
    {
        for (var offset = 0; offset < value.Length; offset++)
            target.Insert(index + offset, value[offset]);
    }

    private static void Insert(List<byte> target, int index, IReadOnlyList<byte> value)
    {
        for (var offset = 0; offset < value.Count; offset++)
            target.Insert(index + offset, value[offset]);
    }

    private static byte[] RemoveRange(byte[] source, int start, int length)
    {
        var result = new byte[source.Length - length];
        source.AsSpan(0, start).CopyTo(result);
        source.AsSpan(start + length).CopyTo(result.AsSpan(start));
        return result;
    }

    private static Tokenization TokenizeOurs(byte[] input, ChunkLayout layout, int chunkSeed)
    {
        var sink = new TokenSink();
        var metrics = new Utf8HtmlTokenizerStateMetrics(Utf8HtmlTokenizer.StateCount);
        var tokenizer = new Utf8HtmlTokenizer(sink, metrics);
        var framed = new Utf8HtmlTokenizerInput(tokenizer);
        var offset = 0;
        foreach (var length in layout.GetChunks(input.Length, chunkSeed))
        {
            framed.Write(input.AsMemory(offset, length));
            offset += length;
        }
        framed.Complete();
        var coverage = String.Join(',', tokenizer.GetStateMetrics().Select(static metric => metric.State).Order());
        return new Tokenization(sink.Tokens, coverage);
    }

    private static IReadOnlyList<Token> TokenizeAngleSharp(byte[] input)
    {
        var html = Encoding.UTF8.GetString(input);
        var sink = new TokenSink();
        using var tokenizer = new HtmlTokenizer(new TextSource(html), HtmlEntityProvider.ResolverExtended);
        while (true)
        {
            ref var token = ref tokenizer.GetStructToken();
            switch (token.Type)
            {
                case HtmlTokenType.Character:
                    sink.Text(Encoding.UTF8.GetBytes(token.Data.ToString()));
                    break;
                case HtmlTokenType.StartTag:
                {
                    var name = token.Name.ToString();
                    sink.StartTag(Encoding.UTF8.GetBytes(name));
                    for (var index = 0; index < token.Attributes.Count; index++)
                    {
                        var attribute = token.Attributes[index];
                        sink.Attribute(
                            Encoding.UTF8.GetBytes(attribute.Name.ToString()),
                            Encoding.UTF8.GetBytes(attribute.Value.ToString())
                        );
                    }
                    sink.StartTagEnd(token.IsSelfClosing);
                    tokenizer.State = name.ToLowerInvariant() switch
                    {
                        "title" or "textarea" => AngleSharp.Html.Parser.HtmlParseMode.RCData,
                        "style" or "xmp" or "iframe" or "noembed" or "noframes" => AngleSharp
                            .Html
                            .Parser
                            .HtmlParseMode
                            .Rawtext,
                        "script" => AngleSharp.Html.Parser.HtmlParseMode.Script,
                        "plaintext" => AngleSharp.Html.Parser.HtmlParseMode.Plaintext,
                        _ => tokenizer.State,
                    };
                    break;
                }
                case HtmlTokenType.EndTag:
                    sink.EndTag(Encoding.UTF8.GetBytes(token.Name.ToString()));
                    break;
                case HtmlTokenType.Comment:
                    sink.Comment(Encoding.UTF8.GetBytes(token.Data.ToString()));
                    break;
                case HtmlTokenType.Doctype:
                    sink.Doctype(
                        new Utf8DoctypeToken(
                            Encoding.UTF8.GetBytes(token.Name.ToString()),
                            Encoding.UTF8.GetBytes(token.PublicIdentifier.ToString()),
                            token.IsPublicIdentifierMissing,
                            Encoding.UTF8.GetBytes(token.SystemIdentifier.ToString()),
                            token.IsSystemIdentifierMissing,
                            token.IsQuirksForced
                        )
                    );
                    break;
                case HtmlTokenType.EndOfFile:
                    sink.EndOfFile();
                    return sink.Tokens;
            }
        }
    }

    private static bool TokensEqual(IReadOnlyList<Token> left, IReadOnlyList<Token> right) => left.SequenceEqual(right);

    private static void LoadCorpus(string? path, List<byte[]> corpus)
    {
        if (path is null)
            return;
        var files = File.Exists(path)
            ? [path]
            : Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Take(256);
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length == 0)
                continue;
            corpus.Add(bytes.Length <= MaximumCandidateBytes ? bytes : bytes[..MaximumCandidateBytes]);
        }
    }

    private static void WriteFailure(Options options, int iteration, byte[] original, byte[] minimized, Failure failure)
    {
        var prefix = Path.Combine(
            options.OutputDirectory,
            $"seed-{options.Seed}-iter-{iteration}-{failure.Kind.ToString().ToLowerInvariant()}"
        );
        File.WriteAllBytes(prefix + ".html", minimized);
        File.WriteAllBytes(prefix + ".original.html", original);
        File.WriteAllText(
            prefix + ".txt",
            $"seed={options.Seed}{Environment.NewLine}iteration={iteration}{Environment.NewLine}"
                + $"kind={failure.Kind}{Environment.NewLine}layout={failure.Layout}{Environment.NewLine}"
                + $"chunkSeed={failure.ChunkSeed}{Environment.NewLine}input={Escape(minimized)}{Environment.NewLine}"
                + $"difference={failure.DescribeDifference()}{Environment.NewLine}{Environment.NewLine}"
                + "AngleSharp / contiguous oracle:"
                + Environment.NewLine
                + String.Join(Environment.NewLine, failure.Expected.Select(static token => token.ToString()))
                + Environment.NewLine
                + Environment.NewLine
                + "ReadOnlyDom / chunked result:"
                + Environment.NewLine
                + String.Join(Environment.NewLine, failure.Actual.Select(static token => token.ToString()))
                + Environment.NewLine
        );
    }

    private static string Escape(ReadOnlySpan<byte> input)
    {
        var result = new StringBuilder(input.Length);
        foreach (var value in input)
        {
            if (value is >= 0x20 and <= 0x7e && value != (byte)'\\')
                result.Append((char)value);
            else
                result.Append("\\x").Append(value.ToString("x2"));
        }
        return result.ToString();
    }

    private static void PrintTokens(IReadOnlyList<Token> tokens)
    {
        foreach (var token in tokens)
            Console.WriteLine("  " + token);
    }

    private enum FailureKind
    {
        Differential,
        Chunking,
    }

    private readonly record struct FuzzOutcome(string Coverage, Failure? Failure);

    private readonly record struct Tokenization(IReadOnlyList<Token> Tokens, string Coverage);

    private sealed record Failure(
        FailureKind Kind,
        ChunkLayout Layout,
        int ChunkSeed,
        IReadOnlyList<Token> Expected,
        IReadOnlyList<Token> Actual,
        string Signature
    )
    {
        public static Failure Create(
            FailureKind kind,
            ChunkLayout layout,
            int chunkSeed,
            IReadOnlyList<Token> expected,
            IReadOnlyList<Token> actual
        )
        {
            var index = FirstDifference(expected, actual);
            var expectedToken = index < expected.Count ? expected[index].ToString() : "<missing>";
            var actualToken = index < actual.Count ? actual[index].ToString() : "<missing>";
            var difference =
                index < expected.Count
                && index < actual.Count
                && expected[index].Kind == "text"
                && actual[index].Kind == "text"
                && expected[index].Value.Equals(actual[index].Value, StringComparison.OrdinalIgnoreCase)
                    ? "text:<case-only>"
                    : $"{expectedToken}|{actualToken}";
            var signature = $"{kind}|{layout.Name}|{index}|{difference}";
            return new Failure(kind, layout, chunkSeed, expected, actual, signature);
        }

        public string DescribeDifference()
        {
            var index = FirstDifference(Expected, Actual);
            var expected = index < Expected.Count ? Expected[index].ToString() : "<missing>";
            var actual = index < Actual.Count ? Actual[index].ToString() : "<missing>";
            return $"token[{index}] expected {expected}, actual {actual}";
        }

        private static int FirstDifference(IReadOnlyList<Token> left, IReadOnlyList<Token> right)
        {
            var length = Math.Min(left.Count, right.Count);
            for (var index = 0; index < length; index++)
            {
                if (left[index] != right[index])
                    return index;
            }
            return length;
        }
    }

    private readonly record struct ChunkLayout(string Name, int FixedSize, bool Randomized)
    {
        public static ChunkLayout Contiguous { get; } = new("contiguous", Int32.MaxValue, false);
        public static IReadOnlyList<ChunkLayout> HostileLayouts { get; } =
        [
            new("bytewise", 1, false),
            new("fixed-2", 2, false),
            new("fixed-3", 3, false),
            new("fixed-7", 7, false),
            new("fixed-16", 16, false),
            new("jitter-31", 31, true),
        ];

        public IEnumerable<int> GetChunks(int total, int seed)
        {
            if (total == 0)
                yield break;
            var random = Randomized ? new Random(seed) : null;
            var remaining = total;
            while (remaining > 0)
            {
                var requested = random is null ? FixedSize : random.Next(1, FixedSize + 1);
                var size = Math.Min(remaining, requested);
                yield return size;
                remaining -= size;
            }
        }

        public override string ToString() => Name;
    }

    private readonly record struct Token(string Kind, string Name, string Value, bool Flag)
    {
        public override string ToString() =>
            Kind switch
            {
                "text" or "comment" => $"{Kind}:{Escape(Encoding.UTF8.GetBytes(Value))}",
                "attribute" => $"attribute:{Name}={Escape(Encoding.UTF8.GetBytes(Value))}",
                "start-end" => Flag ? "start-end:/" : "start-end",
                "doctype" => $"doctype:{Name}|{Value}|quirks={Flag}",
                _ => String.IsNullOrEmpty(Name) ? Kind : $"{Kind}:{Name}",
            };
    }

    private sealed class TokenSink : IUtf8HtmlTokenSink
    {
        private readonly List<Token> _tokens = [];
        private readonly List<Token> _pendingStartTag = [];
        public IReadOnlyList<Token> Tokens => _tokens;
        public Utf8HtmlTokenCapture Capture => Utf8HtmlTokenCapture.Text;

        public void Text(ReadOnlySpan<byte> utf8)
        {
            EnsureValidUtf8(utf8);
            var value = Encoding.UTF8.GetString(utf8);
            if (value.Length == 0)
                return;
            if (_tokens.Count != 0 && _tokens[^1].Kind == "text")
                _tokens[^1] = _tokens[^1] with { Value = _tokens[^1].Value + value };
            else
                _tokens.Add(new Token("text", "", value, false));
        }

        public Utf8HtmlStartTagCapture StartTag(Utf8HtmlName name)
        {
            StartTag(name.Verbatim);
            return Utf8HtmlStartTagCapture.Attributes;
        }

        public void StartTag(ReadOnlySpan<byte> name)
        {
            _pendingStartTag.Clear();
            _pendingStartTag.Add(new Token("start", DecodeName(name), "", false));
        }

        public bool WantsAttribute(Utf8HtmlName name) => true;

        public void Attribute(Utf8HtmlName name, ReadOnlySpan<byte> value, bool valueMayContainReferences) =>
            Attribute(name.Verbatim, AttributeValueDecoding.Decode(value, valueMayContainReferences));

        public void Attribute(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
        {
            EnsureValidUtf8(value);
            _pendingStartTag.Add(new Token("attribute", DecodeName(name), Encoding.UTF8.GetString(value), false));
        }

        public void StartTagEnd(bool selfClosing)
        {
            _tokens.AddRange(_pendingStartTag);
            _tokens.Add(new Token("start-end", "", "", selfClosing));
            _pendingStartTag.Clear();
        }

        public void EndTag(Utf8HtmlName name) => EndTag(name.Verbatim);

        public void EndTag(ReadOnlySpan<byte> name) => _tokens.Add(new Token("end", DecodeName(name), "", false));

        public void Comment(ReadOnlySpan<byte> utf8) =>
            _tokens.Add(new Token("comment", "", Encoding.UTF8.GetString(utf8), false));

        public void Doctype(in Utf8DoctypeToken token)
        {
            var identifiers =
                $"{token.IsPublicIdentifierMissing}:{Encoding.UTF8.GetString(token.PublicIdentifier)}|"
                + $"{token.IsSystemIdentifierMissing}:{Encoding.UTF8.GetString(token.SystemIdentifier)}";
            _tokens.Add(
                new Token(
                    "doctype",
                    Encoding.UTF8.GetString(token.Name).ToLowerInvariant(),
                    identifiers,
                    token.IsQuirksForced
                )
            );
        }

        public void EndOfFile() => _tokens.Add(new Token("eof", "", "", false));

        private static string DecodeName(ReadOnlySpan<byte> name)
        {
            EnsureValidUtf8(name);
            var result = Encoding.UTF8.GetString(name);
            return result.ToLowerInvariant();
        }

        private static void EnsureValidUtf8(ReadOnlySpan<byte> utf8)
        {
            while (!utf8.IsEmpty)
            {
                if (Rune.DecodeFromUtf8(utf8, out _, out var consumed) != OperationStatus.Done)
                    throw new InvalidDataException("Tokenizer emitted invalid UTF-8.");
                utf8 = utf8[consumed..];
            }
        }
    }

    private sealed record Options(
        int Iterations,
        int Seed,
        int MaximumFailures,
        int MinimizeChecks,
        string OutputDirectory,
        string? CorpusPath,
        string? ReproPath
    )
    {
        public static Options Parse(string[] args)
        {
            var iterations = DefaultIterations;
            var seed = DefaultSeed;
            var maximumFailures = 8;
            var minimizeChecks = 1_000;
            string? output = null;
            string? corpus = null;
            string? repro = null;

            for (var index = 0; index < args.Length; index++)
            {
                string Next() =>
                    index + 1 < args.Length
                        ? args[++index]
                        : throw new ArgumentException($"Missing value after {args[index]}.");
                switch (args[index])
                {
                    case "--iterations":
                        iterations = Int32.Parse(Next());
                        break;
                    case "--seed":
                        seed = Int32.Parse(Next());
                        break;
                    case "--max-failures":
                        maximumFailures = Int32.Parse(Next());
                        break;
                    case "--minimize-checks":
                        minimizeChecks = Int32.Parse(Next());
                        break;
                    case "--output":
                        output = Next();
                        break;
                    case "--corpus":
                        corpus = Next();
                        break;
                    case "--repro":
                        repro = Next();
                        break;
                    default:
                        throw new ArgumentException($"Unknown fuzz option: {args[index]}");
                }
            }

            output ??= Path.Combine("artifacts", "fuzz", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{seed}");
            return new Options(iterations, seed, maximumFailures, minimizeChecks, output, corpus, repro);
        }
    }
}
#endif
