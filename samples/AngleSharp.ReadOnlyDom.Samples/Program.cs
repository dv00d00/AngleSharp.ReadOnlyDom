using System.Text;
using AngleSharp.ReadOnlyDom;
using AngleSharp.ReadOnlyDom.Compact;
using AngleSharp.ReadOnlyDom.Compact.Document;
using AngleSharp.ReadOnlyDom.Compact.Parsing;
using AngleSharp.ReadOnlyDom.Compact.Projection;
using AngleSharp.ReadOnlyDom.Compact.Query;
using AngleSharp.ReadOnlyDom.Samples;

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
await StreamingContentJsonExample.RunAsync();

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

    Console.WriteLine($"nodes/attributes : {document.NodeCount}/{document.AttributeCount}");
    Console.WriteLine($"parent retained  : {article.Parent.Exists}");
    Console.WriteLine($"article text     : {Normalize(article.Text())}");
    Console.WriteLine("lifetime         : borrowed spans require the compact document");
}

static void RunConstructionTimeViews()
{
    Heading("COMPACT EOF PROJECTION — construction-time topology, owned results");

    var projectionPlan = CompactProjection
        .First(CompactProjectionSelector.Tag("article").WithId("content"))
        .Field("title", CompactFieldProjection.FirstNormalizedText(CompactProjectionSelector.Tag("h1")), required: true)
        .Field("kind", CompactFieldProjection.SelfAttribute("data-kind"), required: true)
        .Field("text", CompactFieldProjection.SelfNormalizedText())
        .Compile();
    var projection = projectionPlan.Execute(Html);

    Console.WriteLine($"projection JSON  : {CompactProjectionJson.SerializeFirst(projection)}");
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
