const HN_BASE = "https://news.ycombinator.com/";
const MAX_CONCURRENT_PREVIEWS = 3;
const SAFE_COLOR = /^(#[0-9a-f]{3,8}|rgba?\([\d\s,.%/]+\))$/i;

const feedSelect = document.querySelector("#feed");
const intervalSelect = document.querySelector("#interval");
const refreshButton = document.querySelector("#refresh");
const statusLine = document.querySelector("#status");
const list = document.querySelector("#stories");
const template = document.querySelector("#story-template");

const rows = new Map();
let previous = new Map();
let refreshTimer = null;
let inFlight = null;

/**
 * Yields one record per NDJSON line as the response arrives. TextDecoder keeps the tail of a split
 * multi-byte scalar across chunk boundaries, which mirrors what the server does on the way in.
 */
async function* readRecords(response, onBytes) {
  const reader = response.body.getReader();
  const decoder = new TextDecoder("utf-8");
  let pending = "";

  for (;;) {
    const { value, done } = await reader.read();
    if (done) break;
    onBytes(value.byteLength);
    pending += decoder.decode(value, { stream: true });

    let newline;
    while ((newline = pending.indexOf("\n")) >= 0) {
      const line = pending.slice(0, newline).trim();
      pending = pending.slice(newline + 1);
      if (line) yield JSON.parse(line);
    }
  }

  const tail = (pending + decoder.decode()).trim();
  if (tail) yield JSON.parse(tail);
}

function absolute(url) {
  try {
    return new URL(url || "", HN_BASE).href;
  } catch {
    return HN_BASE;
  }
}

function formatAge(unixSeconds) {
  if (!unixSeconds) return "";
  const seconds = Math.max(0, Math.floor(Date.now() / 1000) - unixSeconds);
  if (seconds < 90) return `${seconds}s ago`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 90) return `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 36) return `${hours}h ago`;
  return `${Math.round(hours / 24)}d ago`;
}

function formatBytes(bytes) {
  return bytes >= 1024 ? `${(bytes / 1024).toFixed(1)} KB` : `${bytes} B`;
}

// ---------------------------------------------------------------- story rows

function createRow(story) {
  const row = template.content.firstElementChild.cloneNode(true);
  row.dataset.id = story.id;
  // Every row shows its card frame straight away and fills it in when it scrolls into view, so the list
  // has its final shape from the start instead of shifting under the reader as cards arrive.
  const pane = row.querySelector(".preview");
  pane.hidden = false;
  pane.classList.add("is-pending");
  previewObserver.observe(row);
  return row;
}

function renderRow(row, story) {
  const before = previous.get(story.id);
  row.story = story;
  row.classList.toggle("is-new", previous.size > 0 && before === undefined);

  row.querySelector(".rank").textContent = story.rank || "";

  const title = row.querySelector(".title");
  title.textContent = story.title;
  title.href = absolute(story.url);

  row.querySelector(".site").textContent = story.site ? `(${story.site})` : "";
  row.querySelector(".points").textContent = story.points ? `${story.points} points` : "";
  row.querySelector(".user").textContent = story.user ? `by ${story.user}` : "";

  const age = row.querySelector(".age");
  age.dataset.createdAt = story.createdAt || 0;
  age.textContent = formatAge(story.createdAt);

  const comments = row.querySelector(".comments");
  comments.textContent = story.comments ? `${story.comments} comments` : "discuss";
  comments.href = `${HN_BASE}item?id=${story.id}`;

  const delta = row.querySelector(".delta");
  delta.hidden = true;
  delta.className = "delta";
  if (previous.size === 0) {
    // Nothing to compare against on the first load.
  } else if (before === undefined) {
    delta.textContent = "new";
    delta.classList.add("is-fresh");
    delta.hidden = false;
  } else if (before.rank !== story.rank) {
    const moved = before.rank - story.rank;
    delta.textContent = moved > 0 ? `▲${moved}` : `▼${-moved}`;
    delta.classList.add(moved > 0 ? "is-up" : "is-down");
    delta.hidden = false;
  } else if (before.points !== story.points) {
    delta.textContent = `+${story.points - before.points}`;
    delta.classList.add("is-up");
    delta.hidden = false;
  }
}

function tickAges() {
  for (const age of list.querySelectorAll(".age")) {
    age.textContent = formatAge(Number(age.dataset.createdAt));
  }
}

// ------------------------------------------------------------ preview cards

/** Everything remote is proxied back through this origin, so no card dials a third party. */
function proxied(url) {
  return `/api/image?url=${encodeURIComponent(url)}`;
}

function formatPublished(value) {
  const published = new Date(value);
  return Number.isNaN(published.valueOf()) ? "" : published.toLocaleDateString();
}

function renderCard(pane, card) {
  const fields = card.fields;

  const hero = pane.querySelector(".card-hero");
  if (fields.image && hero.dataset.src !== fields.image) {
    hero.dataset.src = fields.image;
    hero.href = fields.canonical || fields.url;
    const image = hero.querySelector("img");
    image.onerror = () => (hero.hidden = true);
    image.src = proxied(fields.image);
    hero.hidden = false;
  }

  const icon = pane.querySelector(".card-icon");
  if (fields.icon && icon.dataset.src !== fields.icon) {
    icon.dataset.src = fields.icon;
    icon.onerror = () => (icon.hidden = true);
    icon.src = proxied(fields.icon);
    icon.hidden = false;
  }

  pane.querySelector(".card-host").textContent = fields.site || fields.host || "";

  const title = pane.querySelector(".card-title");
  title.textContent = fields.title || card.fallbackTitle;
  title.href = fields.canonical || fields.url;

  pane.querySelector(".card-description").textContent = fields.description || "";

  const byline = [fields.author && `by ${fields.author}`, fields.published && formatPublished(fields.published)]
    .filter(Boolean)
    .join(" · ");
  pane.querySelector(".card-byline").textContent = byline;

  if (fields.accent && SAFE_COLOR.test(fields.accent.trim())) {
    pane.style.setProperty("--accent", fields.accent.trim());
  }
}

function renderStats(pane, stats, age) {
  const note = pane.querySelector(".card-note");
  const read = formatBytes(stats.bytesRead);
  if (age > 0) {
    note.textContent = `cached ${age}s ago · built from ${read} of head`;
  } else {
    note.textContent = stats.stopped
      ? `built from ${read} of head, then the download was abandoned`
      : `built from ${read}`;
  }
  note.hidden = false;
}

async function loadPreview(row) {
  const pane = row.querySelector(".preview");
  const note = pane.querySelector(".card-note");
  const card = { fields: {}, weights: {}, fallbackTitle: row.story.title };

  pane.hidden = false;
  pane.classList.add("is-loading");
  pane.classList.remove("is-empty");
  note.hidden = true;

  try {
    const response = await fetch(`/api/preview?url=${encodeURIComponent(absolute(row.story.url))}`);
    if (!response.ok) throw new Error(`${response.status} ${(await response.text()).trim()}`);
    const snapshotAge = Number(response.headers.get("X-Snapshot-Age") ?? 0);

    for await (const record of readRecords(response, () => {})) {
      // The frame stops being a placeholder as soon as there is anything real to put in it.
      pane.classList.remove("is-pending");
      switch (record.kind) {
        case "source":
          card.fields.url = record.url;
          card.fields.host = record.host;
          renderCard(pane, card);
          break;
        case "meta":
          // Fields arrive in document order, not in order of quality: keep the best one seen.
          if (record.weight >= (card.weights[record.field] ?? 0)) {
            card.weights[record.field] = record.weight;
            card.fields[record.field] = record.value;
            renderCard(pane, card);
          }
          break;
        case "stats":
          renderStats(pane, record, snapshotAge);
          break;
        case "note":
          note.textContent = record.text;
          note.hidden = false;
          break;
      }
    }

    if (!card.fields.icon && card.fields.url) {
      // No declared icon: try the conventional location and let onerror hide it if it is not there.
      card.fields.icon = new URL("/favicon.ico", card.fields.url).href;
      renderCard(pane, card);
    }
  } catch (error) {
    // A page that will not give up a card is not an error worth shouting about: collapse the frame to a
    // single muted line rather than leaving a hole or shifting everything below it.
    pane.classList.add("is-empty");
    note.textContent = "no preview available";
    note.hidden = false;
    console.debug("preview failed", row.story.url, error.message);
  } finally {
    pane.classList.remove("is-loading", "is-pending");
  }
}

// One request per row is polite enough; three at a time keeps a fast scroll from opening thirty sockets.
const previewQueue = [];
let activePreviews = 0;

function pumpPreviews() {
  while (activePreviews < MAX_CONCURRENT_PREVIEWS && previewQueue.length > 0) {
    const row = previewQueue.shift();
    if (!row.isConnected || !row.story) continue;
    activePreviews++;
    loadPreview(row).finally(() => {
      activePreviews--;
      pumpPreviews();
    });
  }
}

const previewObserver = new IntersectionObserver(
  entries => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      previewObserver.unobserve(entry.target);
      if (entry.target.previewRequested) continue;
      entry.target.previewRequested = true;
      previewQueue.push(entry.target);
    }
    pumpPreviews();
  },
  { rootMargin: "200px 0px" }
);

// ------------------------------------------------------------------ refresh

async function refresh() {
  inFlight?.abort();
  const controller = new AbortController();
  inFlight = controller;

  const feed = feedSelect.value;
  const started = performance.now();
  let bytes = 0;
  let count = 0;
  let firstRowMs = null;
  list.classList.add("is-streaming");
  statusLine.textContent = `Streaming ${feed} …`;

  try {
    const response = await fetch(`/api/stories?feed=${encodeURIComponent(feed)}`, { signal: controller.signal });
    if (!response.ok) throw new Error(`${response.status} ${(await response.text()).trim()}`);
    const snapshotAge = Number(response.headers.get("X-Snapshot-Age") ?? 0);

    const current = new Map();
    for await (const story of readRecords(response, chunk => (bytes += chunk))) {
      if (firstRowMs === null) firstRowMs = Math.round(performance.now() - started);
      current.set(story.id, story);

      const row = rows.get(story.id) ?? createRow(story);
      rows.set(story.id, row);
      renderRow(row, story);
      if (list.children[count] !== row) list.insertBefore(row, list.children[count] ?? null);

      count++;
      statusLine.textContent = `Streaming ${feed} … ${count} stories · ${formatBytes(bytes)}`;
    }

    while (list.children.length > count) {
      const dropped = list.lastElementChild;
      previewObserver.unobserve(dropped);
      rows.delete(Number(dropped.dataset.id));
      dropped.remove();
    }

    previous = current;
    const source = snapshotAge > 0 ? `snapshot ${snapshotAge}s old` : "live from news.ycombinator.com";
    statusLine.textContent =
      `${count} stories · ${formatBytes(bytes)} · first row after ${firstRowMs ?? 0} ms · ` +
      `${Math.round(performance.now() - started)} ms total · ${source}`;
  } catch (error) {
    if (error.name !== "AbortError") statusLine.textContent = `Refresh failed: ${error.message}`;
  } finally {
    list.classList.remove("is-streaming");
    if (inFlight === controller) inFlight = null;
  }
}

function scheduleRefresh() {
  clearInterval(refreshTimer);
  const seconds = Number(intervalSelect.value);
  if (seconds > 0) {
    refreshTimer = setInterval(() => {
      if (!document.hidden) refresh();
    }, seconds * 1000);
  }
}

feedSelect.addEventListener("change", () => {
  list.replaceChildren();
  rows.clear();
  previous = new Map();
  refresh();
});
intervalSelect.addEventListener("change", scheduleRefresh);
refreshButton.addEventListener("click", refresh);

setInterval(tickAges, 15000);
scheduleRefresh();
refresh();
