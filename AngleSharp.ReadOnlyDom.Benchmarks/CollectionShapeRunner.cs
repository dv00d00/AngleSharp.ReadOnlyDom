using System.Text;
using AngleSharp.ReadOnlyDom.Html;

namespace AngleSharp.ReadOnlyDom.Benchmarks;

/// <summary>
/// A disposable design probe. It estimates x64 storage from real tree shapes; it is not a benchmark of
/// production collection implementations and should be removed after capacities are selected.
/// </summary>
internal static class CollectionShapeRunner
{
    private static readonly int[] Capacities = [1, 2, 4];

    public static int Run(string[] args)
    {
        var tier = GetOption(args, "--tier") ?? "full";
        var output = GetOption(args, "--output");
        var corpus = BenchmarkCorpus.Load(tier);
        var parser = ReadOnlyParser.CreateParser(ReadOnlyMetadataProfile.Minimal);
        var childCounts = new List<int>();
        var attributeCounts = new List<int>();

        foreach (var source in corpus)
        {
            using var document = parser.ParseReadOnlyDocument(source.Html);
            Visit(document, childCounts, attributeCounts);
        }

        var report = Render(tier, corpus.Count, childCounts, attributeCounts);
        if (output is null)
        {
            Console.WriteLine(report);
        }
        else
        {
            var path = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, report);
            Console.WriteLine(path);
        }

        return 0;
    }

    private static void Visit(IReadOnlyNode node, List<int> childCounts, List<int> attributeCounts)
    {
        childCounts.Add(node.ChildNodes.Length);
        if (node is IReadOnlyElement element && node is not IReadOnlyDocument)
            attributeCounts.Add(element.Attributes.Length);
        foreach (var child in node.ChildNodes)
            Visit(child, childCounts, attributeCounts);
    }

    private static string Render(
        string tier,
        int documents,
        IReadOnlyList<int> childCounts,
        IReadOnlyList<int> attributeCounts
    )
    {
        var result = new StringBuilder();
        result.AppendLine($"# Collection shape estimate ({tier}, {documents} documents)");
        result.AppendLine();
        result.AppendLine(
            "Counts come from parsed Minimal trees. Byte estimates model x64 object/array alignment and geometric overflow growth; validate finalists with BenchmarkDotNet."
        );
        result.AppendLine();
        AppendDistribution(result, "Children per node", childCounts);
        AppendDistribution(result, "Attributes per element", attributeCounts);

        result.AppendLine("## Estimated child-list storage");
        result.AppendLine();
        result.AppendLine(
            "The existing singleton representation handles child one. A list object is allocated only for nodes with at least two children."
        );
        result.AppendLine();
        result.AppendLine("| Inline slots | List objects | Overflow arrays | Estimated bytes |");
        result.AppendLine("| ---: | ---: | ---: | ---: |");
        foreach (var capacity in Capacities)
        {
            var estimate = EstimateChildren(childCounts, capacity);
            result.AppendLine($"| {capacity} | {estimate.Owners:N0} | {estimate.Arrays:N0} | {estimate.Bytes:N0} |");
        }

        result.AppendLine();
        result.AppendLine("## Estimated additional-attribute storage");
        result.AppendLine();
        result.AppendLine(
            "The named map itself stores attribute one. Inline slots below cover only additional attributes. Capacity zero means array-backed overflow, not an attribute-free contract."
        );
        result.AppendLine();
        result.AppendLine("| Inline slots | Maps | Overflow arrays | Estimated bytes |");
        result.AppendLine("| ---: | ---: | ---: | ---: |");
        foreach (var capacity in new[] { 0, 1, 2, 4 })
        {
            var estimate = EstimateAttributes(attributeCounts, capacity);
            result.AppendLine($"| {capacity} | {estimate.Owners:N0} | {estimate.Arrays:N0} | {estimate.Bytes:N0} |");
        }
        result.AppendLine("| no attributes emitted | 0 | 0 | 0 |");
        result.AppendLine();
        result.AppendLine(
            "The last row requires a parser/factory contract that ignores token attributes and exposes an empty map; it is not equivalent to choosing inline capacity zero."
        );
        return result.ToString();
    }

    private static void AppendDistribution(StringBuilder result, string title, IReadOnlyList<int> counts)
    {
        result.AppendLine($"## {title}");
        result.AppendLine();
        result.AppendLine("| Count | Owners | Share |");
        result.AppendLine("| ---: | ---: | ---: |");
        for (var count = 0; count <= 4; count++)
        {
            var owners = counts.Count(value => value == count);
            result.AppendLine($"| {count} | {owners:N0} | {(double)owners / counts.Count:P1} |");
        }
        var more = counts.Count(value => value > 4);
        result.AppendLine($"| 5+ | {more:N0} | {(double)more / counts.Count:P1} |");
        result.AppendLine();
    }

    private static StorageEstimate EstimateChildren(IReadOnlyList<int> counts, int inlineCapacity)
    {
        var owners = counts.Count(value => value >= 2);
        var ownerSize = Align8(16 + inlineCapacity * 8 + 8 + 4);
        return Estimate(counts.Where(value => value >= 2).Select(value => value), owners, ownerSize, inlineCapacity);
    }

    private static StorageEstimate EstimateAttributes(IReadOnlyList<int> counts, int inlineCapacity)
    {
        var values = counts.Where(value => value > 0).Select(value => value - 1).ToArray();
        var ownerSize = Align8(16 + inlineCapacity * 8 + 8 + 4);
        return Estimate(values, values.Length, ownerSize, inlineCapacity);
    }

    private static StorageEstimate Estimate(IEnumerable<int> counts, int owners, int ownerSize, int inlineCapacity)
    {
        var arrays = 0;
        long bytes = (long)owners * ownerSize;
        foreach (var count in counts)
        {
            var overflow = count - inlineCapacity;
            if (overflow <= 0)
                continue;
            arrays++;
            var length = Math.Max(1, inlineCapacity);
            while (length < overflow)
                length *= 2;
            bytes += Align8(24 + length * 8);
        }
        return new StorageEstimate(owners, arrays, bytes);
    }

    private static int Align8(int value) => (value + 7) & ~7;

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private readonly record struct StorageEstimate(int Owners, int Arrays, long Bytes);
}
