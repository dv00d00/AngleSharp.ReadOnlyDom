# Streaming Hacker News reader sample

A live page built from two streaming query plans. The server never holds a document: Hacker News HTML arrives as
UTF-8 bytes, a compiled plan folds it into NDJSON, and each story line is flushed the moment that story's markup
is final. Scroll, and every row unfurls the way a chat client does — the linked page's card metadata streams in
field by field, and the download is abandoned as soon as the head ends.

```powershell
dotnet run --project samples/AngleSharp.ReadOnlyDom.HackerNews -c Release

# Then open the printed URL, or read the endpoints directly:
curl.exe --no-buffer "http://localhost:5000/api/stories?feed=news"
curl.exe --no-buffer "http://localhost:5000/api/preview?url=https%3A%2F%2Fexample.com%2F"
```

## The feed: relating two rows without buffering

The Hacker News list is a layout table from 2007, and it is a good test precisely because it is awkward. A story
is one `tr.athing` row; its score, author, age, and comment count live in the *next* row's `td.subtext`. No element
contains a whole story, so there is nothing to "select" and hand over as a unit.

`StoryFeedPlan` handles that without buffering the page: the title row opens a record, the sibling subtext cell
closes it, and the two are related by the tokenizer's lexical stack in a single pass.

```csharp
var story = html.Descendant("tr").Class("athing").OnStart(/* open a record */, "id");
var titleLine = story.Descendant("span").Class("titleline");
titleLine.Child("a").OnNormalizedText(/* title + href */, "href");   // Child: the site anchor is deeper

var subtext = html.Descendant("td").Class("subtext").OnEnd(/* publish the record */);
subtext.Descendant("span").Class("score").OnNormalizedText(/* 459 points */);
subtext.Descendant("span").Class("age").OnStart(/* the Unix time in the tooltip */, "title");
```

Two details are worth stealing:

- `Child("a")` under `span.titleline` matches the story link only. The source link is one level deeper inside
  `span.sitebit`, so a descendant query would have picked up both.
- The subline's anchors are untyped — hide, user, age, and comments all look alike — so the plan reads all of them
  and the state decides by text: `215 comments` counts, `discuss` means none.

The page's ages tick between refreshes because the plan publishes the submission instant from the `title` tooltip
rather than the phrase "9 hours ago" — a rendered string would go stale the second it arrived.

## The publication frontier

Both plans write through `NdjsonPublisher`, which builds a record in a scratch buffer and copies it into a
`PublishableUtf8Buffer` only once it is whole. `ExecuteBackpressuredAsync` publishes exactly that marked prefix, so
the response body always ends on a complete line: whatever the client has received, it can parse and render, and a
client that stops reading applies backpressure through to the upstream socket.

The endpoints ask for `flushThreshold: 1` instead of the 16 KiB default. Batching would be cheaper, and would also
defeat the point, which is that row 1 renders before row 30 exists.

## The preview: a card, and then stop reading

`PreviewPlan` reads the head of a linked page and nothing else. `meta` and `link` keys are matched in the callback
rather than by one query node per key — one node with three projected attributes covers a dozen spellings of the
same four fields, and adding another costs a branch instead of a node.

Fields do not arrive in order of quality, so each one is published with a weight: `og:title` (3) outranks
`twitter:title` (2) outranks `<title>` (1). The server suppresses anything that cannot improve on what it has
already sent, and the browser keeps the best value seen and re-renders — no buffering of the head to decide.

When the head ends, the card is final and the rest of the document is worthless, so the sample stops reading. That
is done by ending the *input stream* (`EarlyStopStream`), not by cancelling the execution — and the difference is
load-bearing. The publishing loop copies a publishable prefix into the response, flushes it, and only then marks it
consumed; cancel between those steps and the prefix is on the wire while the buffer still thinks it is pending, so
the tail gets written twice. Ending the input lets the tokenizer finish normally on end-of-input, and every record
is published exactly once. A closing `stats` record reports what the card actually cost — typically 4 KB of a page
that weighs several hundred.

Previews run through the encoded lane (`ExecuteEncodedBackpressuredAsync`): a random page on the internet is not
necessarily UTF-8, so the transport charset wins when it names one the runtime knows, and otherwise the document's
BOM or `meta` declaration decides.

In the browser, responses are read with `TextDecoder(..., { stream: true })`, which holds the tail of a split
multi-byte scalar across chunk boundaries — the same problem the tokenizer solves on the way in, one layer up.
Card text is set with `textContent`, never as markup.

## Talking to someone else's server

The sample fetches URLs the page chooses, so `UpstreamFetcher` is its outbound boundary:

- http and https only, on their default ports, no credentials in the URL.
- Redirects are followed by hand (four hops) and re-checked at each one.
- Every socket is connected through a `ConnectCallback` that resolves the host and refuses private, loopback,
  link-local, and carrier-grade-NAT addresses. Validating at connect time rather than at parse time is what keeps a
  name that re-resolves between the two from slipping through.
- Card images are re-served through `/api/image` behind the same boundary and capped at 5 MiB, so opening a preview
  never dials a third party from the browser.

Cards load only for rows scrolled into view, three requests at a time. Feeds are cached for 15 seconds and cards for
10 minutes, so a refreshing tab, several tabs, and a scroll back up the list cost the upstream sites one request
rather than one each.

## What this sample is not

It is not a Hacker News client, a readability implementation, or a proxy. The plans observe lexical start/end-tag
topology rather than browser-corrected tree topology, so omitted end tags and foster parenting can differ from a
DOM converter — use the retained DOM lanes when corrected tree semantics matter. A page that declares its card
metadata below the head, or only from script, gets no card here.
