const root = document.querySelector("#document");
const path = document.querySelector("#path");
const status = document.querySelector("#status");
const back = document.querySelector("#back");
const home = document.querySelector("#home");
const generatedMarkdown = document.querySelector("#source");
const navigationHistory = [];
let currentDocument = null;

root.addEventListener("click", event => {
  const link = event.target.closest("a[data-document-link]");
  if (!link || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey)
    return;

  event.preventDefault();
  navigate(link.href);
});

back.addEventListener("click", () => {
  const previous = navigationHistory.pop();
  updateBackButton();
  if (previous) navigate(previous, false);
});

home.addEventListener("click", () => navigate("/pages/index.html"));

async function navigate(value, remember = true) {
  const target = localPageUrl(value, currentDocument ?? location.href);
  if (!target) {
    status.textContent = "Only the checked-in /pages/*.html examples can be opened here.";
    return;
  }

  const previous = currentDocument;
  status.textContent = "Loading…";
  try {
    const response = await fetch(`/markdown?page=${encodeURIComponent(target.pathname)}`, {
      headers: { "Accept": "text/markdown" }
    });
    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
    const markdown = await response.text();
    if (remember && previous && previous.href !== target.href)
      navigationHistory.push(previous);
    currentDocument = target;
    path.textContent = `${target.pathname}${target.hash}`;
    generatedMarkdown.value = markdown;
    renderMarkdown(markdown);
    updateBackButton();
    status.textContent = `${markdown.length.toLocaleString()} Markdown characters streamed`;
    window.history.replaceState(null, "", `#${encodeURIComponent(target.pathname + target.hash)}`);
    if (target.hash)
      document.getElementById(target.hash.slice(1))?.scrollIntoView();
    else
      scrollTo({ top: 0, behavior: "auto" });
  } catch (error) {
    status.textContent = `Navigation failed: ${error.message}`;
  }
}

function renderMarkdown(source) {
  const lines = source.replaceAll("\r\n", "\n").split("\n");
  const fragment = document.createDocumentFragment();
  let index = 0;

  while (index < lines.length) {
    const line = lines[index];
    if (!line.trim()) {
      index++;
      continue;
    }

    if (line.startsWith("```")) {
      const code = [];
      index++;
      while (index < lines.length && !lines[index].startsWith("```"))
        code.push(lines[index++]);
      if (index < lines.length) index++;
      const pre = document.createElement("pre");
      const value = document.createElement("code");
      value.textContent = code.join("\n");
      pre.append(value);
      fragment.append(pre);
      continue;
    }

    const heading = /^(#{1,6})\s+(.+)$/.exec(line);
    if (heading) {
      const element = document.createElement(`h${heading[1].length}`);
      appendInline(element, heading[2]);
      element.id = slug(heading[2]);
      fragment.append(element);
      index++;
      continue;
    }

    if (line.startsWith("- ")) {
      const list = document.createElement("ul");
      while (index < lines.length && lines[index].startsWith("- ")) {
        const item = document.createElement("li");
        appendInline(item, lines[index].slice(2));
        list.append(item);
        index++;
      }
      fragment.append(list);
      continue;
    }

    const paragraph = [];
    while (
      index < lines.length
      && lines[index].trim()
      && !lines[index].startsWith("#")
      && !lines[index].startsWith("- ")
      && !lines[index].startsWith("```")
    ) {
      paragraph.push(lines[index++].trim());
    }
    const element = document.createElement("p");
    appendInline(element, paragraph.join(" "));
    fragment.append(element);
  }

  root.replaceChildren(fragment);
}

function appendInline(parent, source) {
  const pattern = /\[([^\]]+)\]\(([^)]+)\)|`([^`]+)`|\*\*([^*]+)\*\*|\*([^*]+)\*/g;
  let offset = 0;
  for (const match of source.matchAll(pattern)) {
    parent.append(document.createTextNode(source.slice(offset, match.index)));
    if (match[1] !== undefined) {
      appendLink(parent, match[1], match[2]);
    } else {
      const element = document.createElement(match[3] !== undefined ? "code" : match[4] !== undefined ? "strong" : "em");
      element.textContent = match[3] ?? match[4] ?? match[5];
      parent.append(element);
    }
    offset = match.index + match[0].length;
  }
  parent.append(document.createTextNode(source.slice(offset)));
}

function appendLink(parent, label, href) {
  let target;
  try {
    target = new URL(href, currentDocument ?? location.href);
  } catch {
    parent.append(document.createTextNode(label));
    return;
  }

  const link = document.createElement("a");
  link.textContent = label;
  link.href = target.href;
  if (localPageUrl(target, currentDocument ?? location.href)) {
    link.dataset.documentLink = "";
  } else if (target.protocol === "http:" || target.protocol === "https:") {
    link.target = "_blank";
    link.rel = "noreferrer noopener";
  } else {
    parent.append(document.createTextNode(label));
    return;
  }
  parent.append(link);
}

function localPageUrl(value, base) {
  let target;
  try {
    target = new URL(value, base);
  } catch {
    return null;
  }
  return target.origin === location.origin
    && target.pathname.startsWith("/pages/")
    && target.pathname.endsWith(".html")
    ? target
    : null;
}

function slug(value) {
  return value.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
}

function updateBackButton() {
  back.disabled = navigationHistory.length === 0;
}

const initial = location.hash.startsWith("#%2F")
  ? decodeURIComponent(location.hash.slice(1))
  : "/pages/index.html";
navigate(initial, false);
