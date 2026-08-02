using System.Text;

const string ExampleHtml = """
    <!doctype html>
    <html>
      <body>
        <nav>Documentation navigation.</nav>
        <article id="content">
          <h1>Parsing <em>real</em> HTML</h1>
          <p>Use the <a href="/parser">HTML parser</a>, not regex.</p>
          <ul><li>Correct tables</li><li>Malformed markup</li></ul>
          <p><img alt="Parser diagram:">The diagram remains useful without images.</p>
          <pre><code>parse --input page.html</code></pre>
          <aside>This advertisement is intentionally omitted from Markdown.</aside>
        </article>
        <script>throw new Error("not content")</script>
      </body>
    </html>
    """;

var html = args.Length == 0 ? ExampleHtml : await File.ReadAllTextAsync(args[0]);

Write("STREAMING TEXT EXTRACTION", StreamingTextExtractionExample.Extract(Encoding.UTF8.GetBytes(html)));
Write("COMPACT DOM TEXT EXTRACTION", CompactTextExtractionExample.Extract(html));
Write(
    "COMPACT MARKDOWN PROJECTION",
    CompactMarkdownProjectionExample.ProjectArticle(html, "content", "nav", "aside")
);

static void Write(string title, string value)
{
    Console.WriteLine();
    Console.WriteLine(title);
    Console.WriteLine(new string('-', title.Length));
    Console.WriteLine(value);
}
