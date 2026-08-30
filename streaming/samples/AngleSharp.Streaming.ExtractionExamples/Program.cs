using System.Text;
using AngleSharp.Streaming.ExtractionExamples;

const string ExampleHtml = """
    <!doctype html><html><body><article><h1>Parsing <em>real</em> HTML</h1>
    <p>Use the <a href="/parser">HTML parser</a>, not regex.</p></article></body></html>
    """;

var html = args.Length == 0 ? ExampleHtml : await File.ReadAllTextAsync(args[0]);
Console.WriteLine(StreamingTextExtractionExample.Extract(Encoding.UTF8.GetBytes(html)));
