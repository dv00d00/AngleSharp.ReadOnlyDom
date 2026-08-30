const HN_BASE = "https://news.ycombinator.com/";
const REFRESH_SECONDS = 60;
const MAX_CONCURRENT_PREVIEWS = 3;
const SAFE_COLOR = /^(#[0-9a-f]{3,8}|rgba?\([\d\s,.%/]+\))$/i;

const feedTabs = document.querySelector("#feeds");
const refreshButton = document.querySelector("#refresh");
const statusLine = document.querySelector("#status");
const list = document.querySelector("#stories");
const template = document.querySelector("#story-template");

const rows = new Map();
let feed = "news";
let previous = new Map();
let inFlight = null;

/** Yields one record per NDJSON line as the response arrives. */
async function* readRecords(response) {
  const reader = response.body.getReader();
  const decoder = new TextDecoder("utf-8");
  let pending = "";

  for (;;) {
    const { value, done } = await reader.read();
    if (done) break;
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

// ---------------------------------------------------------------- story rows

function createRow(story) {
  const row = template.content.firstElementChild.cloneNode(true);
  row.dataset.id = story.id;
  // The card frame is reserved up front and filled when the row scrolls into view, so the list keeps its
  // shape instead of shifting as cards arrive.
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
  if (previous.size === 0) return;

  if (before === undefined) {
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

/** Remote images are re-served from this origin. */
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

async function loadPreview(row) {
  const pane = row.querySelector(".preview");
  const card = { fields: {}, weights: {}, fallbackTitle: row.story.title };

  try {
    const response = await fetch(`/api/preview?url=${encodeURIComponent(absolute(row.story.url))}`);
    if (!response.ok) throw new Error(String(response.status));

    for await (const record of readRecords(response)) {
      pane.classList.remove("is-pending");
      if (record.kind === "source") {
        card.fields.url = record.url;
        card.fields.host = record.host;
        renderCard(pane, card);
      } else if (record.kind === "meta" && record.weight >= (card.weights[record.field] ?? 0)) {
        // Fields arrive in document order, not in order of quality: keep the best one seen.
        card.weights[record.field] = record.weight;
        card.fields[record.field] = record.value;
        renderCard(pane, card);
      }
    }

    if (!card.fields.icon && card.fields.url) {
      card.fields.icon = new URL("/favicon.ico", card.fields.url).href;
      renderCard(pane, card);
    }
    if (!card.fields.title && !card.fields.description && !card.fields.image) pane.hidden = true;
  } catch {
    pane.hidden = true;
  } finally {
    pane.classList.remove("is-pending");
  }
}

// Three at a time, so a fast scroll does not open thirty sockets.
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

/** `manual` revalidates instead of reading the browser's copy, so the button always reaches the server. */
async function refresh({ manual = false } = {}) {
  inFlight?.abort();
  const controller = new AbortController();
  inFlight = controller;

  refreshButton.classList.add("is-busy");
  refreshButton.disabled = true;
  statusLine.textContent = "updating…";
  let count = 0;

  try {
    const response = await fetch(`/api/stories?feed=${encodeURIComponent(feed)}`, {
      signal: controller.signal,
      cache: manual ? "no-cache" : "default"
    });
    if (!response.ok) throw new Error(String(response.status));

    const current = new Map();
    for await (const story of readRecords(response)) {
      current.set(story.id, story);

      const row = rows.get(story.id) ?? createRow(story);
      rows.set(story.id, row);
      renderRow(row, story);
      if (list.children[count] !== row) list.insertBefore(row, list.children[count] ?? null);
      count++;
    }

    while (list.children.length > count) {
      const dropped = list.lastElementChild;
      previewObserver.unobserve(dropped);
      rows.delete(Number(dropped.dataset.id));
      dropped.remove();
    }

    previous = current;
    statusLine.textContent = `updated ${new Date().toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}`;
  } catch (error) {
    if (error.name !== "AbortError") statusLine.textContent = "could not reach the feed";
  } finally {
    if (inFlight === controller) inFlight = null;
    refreshButton.classList.remove("is-busy");
    refreshButton.disabled = false;
  }
}

feedTabs.addEventListener("click", event => {
  const tab = event.target.closest("button[data-feed]");
  if (!tab || tab.dataset.feed === feed) return;

  for (const button of feedTabs.children) button.classList.toggle("is-active", button === tab);
  feed = tab.dataset.feed;
  list.replaceChildren();
  rows.clear();
  previous = new Map();
  refresh();
});

refreshButton.addEventListener("click", () => refresh({ manual: true }));

setInterval(() => {
  if (!document.hidden) refresh();
}, REFRESH_SECONDS * 1000);
setInterval(tickAges, 15000);
refresh();
