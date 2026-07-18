# HTML folding proxy

A deliberately naive show-off for the completed-element UTF-8 fold. It streams upstream bytes through the native
tokenizer, writes borrowed completed-element spans into one UTF-8 output buffer, and decodes no element strings.

```powershell
dotnet run --project samples/AngleSharp.ReadOnlyDom.MarkdownProxy -c Release

# Open http://localhost:5000 and choose Markdown or plain text.
curl.exe "http://localhost:5000/markdown?url=https://example.com"
curl.exe "http://localhost:5000/text?url=https://example.com"
curl.exe -X POST http://localhost:5000/markdown `
  -H "Content-Type: text/html; charset=utf-8" `
  --data-binary "<html><body><h1>Hello</h1><p>From <b>RODOM</b>.</p></body></html>"

# POST supports the same opt-in lane:
curl.exe -X POST "http://localhost:5000/markdown?stream=true" `
  -H "Content-Type: text/html; charset=utf-8" `
  --data-binary "<html><body><article><p>Publishable at block close.</p></article></body></html>"
```

The UI includes presets for Example Domain, Wikipedia's HTTP status-code tables, Hacker News, the .NET Blog, and a local
deterministic demo. Its format selector switches between the richer Markdown fold and a smaller plain-text fold suitable
for search or LLM ingestion. The text fold normalizes whitespace, preserves semantic paragraph and table-cell separators,
and skips script-like content. The tiny dependency-free browser renderer handles headings, paragraphs, lists, quotes,
fenced code, proxied images, links, and GitHub-style Markdown tables. Preview links stay inside the converter, and the Back
button returns through viewer history. Redirects are followed explicitly for at most ten HTTP(S) hops; the final URL is
returned to the UI so relative links and images resolve against the landing page.

Hacker News demonstrates a query-directed layout profile: `table#hnmain` is not treated as a data table, while
`tr.athing` and `span.subline` become linked story blocks. Actual tables with cells continue through the GFM table fold.
Images are streamed through `/asset`, limited to `image/*` responses and known content lengths up to 20 MB. YARP would be
unnecessary machinery for this single read-only proxy route.

This is intentionally not a correct general HTML-to-Markdown converter. It assumes explicit `html` markup and UTF-8,
extracts a handful of block elements, loses most inline formatting, may duplicate nested block text, ignores nested
tables and embedded data-URI images, and permits arbitrary HTTP(S) upstream URLs. The asset length limit cannot bound a
chunked upstream response. Keep it bound to localhost.

Both endpoints use the backpressured lane by default. Add `stream=false` to buffer the usually small result and send it
with one write. In the streaming lane, synchronous tokenizer/query work marks final UTF-8 prefixes safe to publish, and
the outer async pump flushes them to `HttpResponse.BodyWriter` before consuming more bounded input slices. No callback
blocks or becomes asynchronous.

The Markdown converter's "prefer the first article, otherwise use the whole page" heuristic is an example of a real
publication frontier. Before an `article` appears, broad-page output remains tentative because it may be discarded. Once
the article is selected, completed Markdown blocks can be marked publishable and flushed. A page with no article
necessarily retains its fallback until EOF; generic rewriters whose output is immediately publishable remain bounded.
