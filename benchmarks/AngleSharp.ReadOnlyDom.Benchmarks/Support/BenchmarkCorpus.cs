namespace AngleSharp.ReadOnlyDom.Benchmarks.Support;

internal sealed record CorpusDocument(string Name, string Html);

internal static class BenchmarkCorpus
{
    private static readonly string[] SmallNames = ["nytimes", "stackoverflow", "wikipedia", "amazon", "w3"];

    public static IReadOnlyList<CorpusDocument> Load(string tier)
    {
        var directory = FindCorpusDirectory();
        var files = Directory.GetFiles(directory, "*.html").OrderBy(Path.GetFileName).ToArray();
        if (tier.Equals("small", StringComparison.OrdinalIgnoreCase))
        {
            var selected = files
                .Where(file => SmallNames.Any(name => Path.GetFileNameWithoutExtension(file).Contains(name)))
                .ToArray();
            if (selected.Length != SmallNames.Length)
            {
                throw new InvalidOperationException(
                    $"Expected {SmallNames.Length} small-corpus documents, found {selected.Length} in '{directory}'."
                );
            }

            files = selected;
        }
        else if (!tier.Equals("full", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Corpus tier must be 'small' or 'full'.", nameof(tier));
        }

        return files
            .Select(file => new CorpusDocument(Path.GetFileNameWithoutExtension(file), File.ReadAllText(file)))
            .ToArray();
    }

    public static IReadOnlyList<CorpusDocument> LoadLargestAnonymized(int count)
    {
        var directory = FindCorpusDirectory();
        return Directory
            .GetFiles(directory, "*.html")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.Length)
            .Take(count)
            .Select((file, index) => new CorpusDocument($"Large{(char)('A' + index)}", File.ReadAllText(file.FullName)))
            .ToArray();
    }

    private static string FindCorpusDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "dom", "tests", "AngleSharp.ReadOnlyDom.Tests", "temp");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the checked-in dom/tests/AngleSharp.ReadOnlyDom.Tests/temp corpus."
        );
    }
}
