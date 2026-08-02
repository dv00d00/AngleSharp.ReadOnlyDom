using System.Text;
using AngleSharp.ReadOnlyDom;
using AngleSharp.ReadOnlyDom.Compact;

const string Html = """
    <!doctype html>
    <html>
      <body>
        <article id="content" class="guide" data-kind="sample">
          <h1>Parsing <em>real</em> HTML </h1>
          <p>Use the <a href="/parser">HTML parser</a>, not regex. </p>
          <ul><li>Correct tables </li><li>Malformed markup</li></ul>
        </article>
      </body>
    </html>
    """;

RunReadOnlyDom();
RunCompactDom();
RunConstructionTimeViews();
StreamingQueryExamples.Run();
StreamingOutcomeExample.Run();

static void RunReadOnlyDom()
{
    Heading("RODOM — retained read-only object graph");

    var parser = ReadOnlyParser.CreateParser(ReadOnlyMetadataProfile.SourceMapped);
    using var document = parser.ParseReadOnlyDocument(Html);
    var article = document.QueryOne(static node => node.TagId("article", "content"))!;

    Console.WriteLine($"metadata profile : {document.MetadataProfile}");
    Console.WriteLine($"source metadata  : {document.TryGetSourceMetadata(out _)}");
    Console.WriteLine($"article text     : {Normalize(article.GetTextContent())}");
    Console.WriteLine($"guide articles   : {document.CountTagClass("article", "guide")}");
    Console.WriteLine("lifetime         : nodes and source-backed values live until document disposal");
}

static void RunCompactDom()
{
    Heading("COMPACT — retained columnar document");

    var parser = CompactParser.CreateParser(CompactMetadataOptions.ParentLinks);
    using var document = parser.ParseCompactDocument(Html);
    var article = document.Elements("article").WithAttribute("id", "content").First();

    Console.WriteLine($"layout           : {document.Layout}");
    Console.WriteLine($"nodes/attributes : {document.NodeCount}/{document.AttributeCount}");
    Console.WriteLine($"parent retained  : {article.Parent.Exists}");
    Console.WriteLine($"article text     : {Normalize(article.Text())}");
    Console.WriteLine("lifetime         : borrowed spans require the compact document");
}

static void RunConstructionTimeViews()
{
    Heading("COMPACT STREAMING — construction-time results, no escaping DOM");

    var aggregatePlan = CompactAggregate
        .First(CompactAggregateSelector.Tag("article").WithId("content"))
        .Field(
            "title",
            CompactAggregateProjection.FirstNormalizedText(CompactAggregateSelector.Tag("h1")),
            required: true
        )
        .Field("kind", CompactAggregateProjection.SelfAttribute("data-kind"), required: true)
        .Field("text", CompactAggregateProjection.SelfNormalizedText())
        .Compile();
    var aggregate = aggregatePlan.Execute(Html);

    Console.WriteLine($"aggregate JSON   : {aggregate.ToJson()}");
    Console.WriteLine(
        $"aggregate work   : {aggregate.Counters.TokensProcessed} tokens, "
            + $"{aggregate.Counters.NodesMaterialized} topology nodes, "
            + $"{aggregate.Counters.RowsProduced} owned row"
    );
    Console.WriteLine("lifetime         : returned values are owned; the construction arena is already disposed");
    Console.WriteLine("input boundary   : current APIs still consume a rooted string and parse through EOF");
}

static string Normalize(string value)
{
    var output = new StringBuilder(value.Length);
    var pendingSpace = false;
    foreach (var character in value)
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
    return output.ToString();
}

static void Heading(string title)
{
    Console.WriteLine();
    Console.WriteLine(title);
    Console.WriteLine(new string('-', title.Length));
}
