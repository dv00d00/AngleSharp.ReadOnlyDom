# Hacker News reader

A Hacker News front end built on two streaming query plans. Nothing is buffered: list HTML is folded into NDJSON one
story per line, and each row unfurls a link-preview card as it scrolls into view.

Needs a .NET 11 SDK (`net11.0`, platform async on).

```powershell
dotnet run --project samples/AngleSharp.ReadOnlyDom.HackerNews -c Release

# with the library on the same async lane
dotnet run --project samples/AngleSharp.ReadOnlyDom.HackerNews -c Release -p:Net11Lane=true -p:Net11Async=true
```

## Endpoints

| | |
| --- | --- |
| `GET /api/stories?feed=news\|newest\|best\|ask\|show\|jobs` | NDJSON, one story per line |
| `GET /api/preview?url=` | NDJSON card fields for a linked page |
| `GET /api/image?url=` | image proxy, `image/*` only, 5 MiB cap |

## Notes

**Feed.** A story is one `tr.athing`; its score, author, age and comments live in the *next* row's `td.subtext`. No
element contains a whole story, so the record opens on the title row and closes when the subtext cell ends — one
lexical pass, nothing buffered. `Child("a")` under `span.titleline` excludes the source link one level deeper in
`span.sitebit`. The subline's anchors are untyped, so text decides: `215 comments` counts, `discuss` means none. Age
is published as the tooltip's Unix time, so rows age themselves between refreshes.

**Frontier.** `NdjsonPublisher` marks a line publishable only once whole, so the body always ends on a complete
record and backpressure reaches the upstream socket. `flushThreshold: 1` instead of 16 KiB.

**Preview.** `meta`/`link` keys are matched in the callback, not by one node per key. Fields carry a weight
(`og:` 3 > `twitter:` 2 > `<title>` 1); the server drops anything that cannot improve on what it sent, the browser
keeps the best seen. At `</head>` the card is final and reading stops — usually ~4 KB of a page weighing hundreds.
Encoded lane, so transport charset wins, then BOM or `meta`.

**Stop by ending input, not by cancelling.** The publish loop copies a prefix, flushes, then marks it consumed;
cancelling in between leaves those bytes on the wire while the buffer still reports them pending, and the tail goes
out twice. `EarlyStopStream` returns 0 instead, so the tokenizer ends normally and every record publishes once.

**Caching.** A response is never fresher than the snapshot behind it, so `max-age` is the snapshot lifetime and
`Age` is how much of it is spent — client and server copies expire together instead of stacking. Snapshot hits carry
a strong ETag and answer 304; a live streaming response carries freshness only, since no validator exists until the
body is done. Proxied images pass upstream `ETag`/`Last-Modified` through, forward the browser's conditional request
and relay the 304. UI assets are not fingerprinted, so they revalidate (`private, no-cache`). Errors are `no-store`.
The refresh button revalidates rather than reading the browser's copy.

**Outbound.** http/https on default ports, no userinfo; redirects by hand (4 hops), re-checked each time; every
socket goes through a `ConnectCallback` that refuses private, loopback, link-local and CGNAT addresses at connect
time, not parse time. Card images are re-served from this origin. Feeds cache 15 s, cards 10 min. Cards load three
at a time.

**Platform async.** `Features=runtime-async=on` is the switch; `UseRuntimeAsync` alone is a no-op and SDK
11.0.100-preview.7 has no property for it. Restore does not flow `AdditionalProperties` across a project reference,
so `Net11Lane`/`Net11Async` must be global `-p:` properties or restore fails NETSDK1005. Emitted state machines: 0
vs 3 for this app (43,008 B vs 52,224 B), 0 vs 14 for the library (293,376 B vs 309,760 B). Whether it is *faster*
is unmeasured here — earlier preview-6 runs gained 1–4% on stream shapes while the preview JIT lost 8–12% on the
synchronous tokenizer loop.

**Limits.** Lexical start/end-tag topology, not corrected tree topology: omitted end tags and foster parenting can
differ from a DOM converter. A page that declares card metadata below the head, or only from script, gets no card.
