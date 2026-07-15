# HTML to Markdown proxy

A deliberately naive show-off for the completed-element UTF-8 fold. It streams upstream bytes through the native
tokenizer, writes borrowed completed-element spans into one UTF-8 output buffer, and decodes no element strings.

```powershell
dotnet run --project samples/AngleSharp.ReadOnlyDom.MarkdownProxy -c Release

# Open http://localhost:5000 for the editable Markdown + rendered preview UI.
curl.exe "http://localhost:5000/markdown?url=https://example.com"
curl.exe "http://localhost:5000/markdown?stream=true&url=https://example.com"
curl.exe -X POST http://localhost:5000/markdown `
  -H "Content-Type: text/html; charset=utf-8" `
  --data-binary "<html><body><h1>Hello</h1><p>From <b>RODOM</b>.</p></body></html>"

# POST supports the same opt-in lane:
curl.exe -X POST "http://localhost:5000/markdown?stream=true" `
  -H "Content-Type: text/html; charset=utf-8" `
  --data-binary "<html><body><article><p>Committed at block close.</p></article></body></html>"
```

The UI includes presets for Example Domain, Wikipedia's HTTP status-code tables, Hacker News' table layout, the .NET
Blog, and a local deterministic demo. The tiny dependency-free browser renderer handles headings, paragraphs, lists,
quotes, fenced code, images, and GitHub-style Markdown tables.

This is intentionally not a correct general HTML-to-Markdown converter. It assumes explicit `html` markup and UTF-8,
extracts a handful of block elements, loses most inline formatting, may duplicate nested block text, ignores nested
tables and embedded data-URI images, and permits arbitrary
HTTP(S) upstream URLs. Keep it bound to localhost.

The default endpoint buffers the usually small Markdown result and sends it with one write. Add `stream=true` to use the
backpressured lane: synchronous tokenizer/query work produces irrevocable UTF-8 prefixes, and the outer async pump flushes
them to `HttpResponse.BodyWriter` before consuming more bounded input slices. No callback blocks or becomes asynchronous.

The converter's "prefer the first article, otherwise use the whole page" heuristic is an example of a real commit
frontier. Before an `article` appears, broad-page output remains tentative because it may be discarded. Once the article
is selected, completed Markdown blocks can be committed and flushed. A page with no article necessarily retains its
fallback until EOF; generic rewriters with immediately irrevocable output remain bounded throughout.
