using System.Text;
using AngleSharp.Streaming.ExtractionExamples;

const string ExampleHtml = """
    <!doctype html>
    <html>
      <body>
        <article>
          <h1>Parsing <em>real</em> HTML</h1>
          <p>Use the <a href="/parser">HTML parser</a>, not regex.</p>
          <ul><li>Correct tables</li><li>Malformed markup</li></ul>
          <p><img alt="Parser diagram:">The diagram remains useful without images.</p>
          <pre><code>parse --input page.html</code></pre>
        </article>
        <script>throw new Error("not content")</script>
      </body>
    </html>
    """;

var html = args.Length == 0 ? ExampleHtml : await File.ReadAllTextAsync(args[0]);
var extracted = StreamingTextExtractionExample.Extract(Encoding.UTF8.GetBytes(html));

Console.WriteLine("STREAMING TEXT EXTRACTION");
Console.WriteLine("-------------------------");
Console.WriteLine(extracted);
