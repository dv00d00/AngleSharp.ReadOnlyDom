const form = document.querySelector("#fetch-form");
const urlInput = document.querySelector("#url");
const markdown = document.querySelector("#markdown");
const preview = document.querySelector("#preview");
const status = document.querySelector("#status");
const backButton = document.querySelector("#back");
const format = document.querySelector("#format");
const sourceTitle = document.querySelector("#source-title");
const renderButton = document.querySelector("#render");
const navigationHistory = [];
let currentDocumentUrl = null;

renderButton.addEventListener("click", render);
format.addEventListener("change", () => {
  updateFormatUi();
  if (currentDocumentUrl) form.requestSubmit();
  else render();
});
document.querySelectorAll("[data-url]").forEach(button => {
  button.addEventListener("click", () => {
    navigationHistory.length = 0;
    updateBackButton();
    const value = button.dataset.url;
    urlInput.value = value.startsWith("/") ? new URL(value, location.href) : value;
    form.requestSubmit();
  });
});

form.addEventListener("submit", async event => {
  event.preventDefault();
  const url = new URL(urlInput.value, location.href).href;
  status.textContent = `Folding ${url} …`;
  form.querySelector("button[type=submit]").disabled = true;
  try {
    const response = await fetch(`/${format.value}?url=${encodeURIComponent(url)}`);
    if (!response.ok) throw new Error(`${response.status} ${await response.text()}`);
    markdown.value = await response.text();
    currentDocumentUrl = response.headers.get("X-Source-Url") ?? url;
    urlInput.value = currentDocumentUrl;
    render();
    const label = format.value === "markdown" ? "Markdown" : "plain-text";
    status.textContent = `${markdown.value.length.toLocaleString()} ${label} characters.`;
  } catch (error) {
    status.textContent = `Conversion failed: ${error.message}`;
  } finally {
    form.querySelector("button[type=submit]").disabled = false;
  }
});

backButton.addEventListener("click", () => {
  const previous = navigationHistory.pop();
  updateBackButton();
  if (!previous) return;
  urlInput.value = previous;
  form.requestSubmit();
});

preview.addEventListener("click", event => {
  const link = event.target.closest("a[href]");
  if (!link || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey)
    return;

  let target;
  try {
    target = new URL(link.getAttribute("href"), currentDocumentUrl ?? location.href);
  } catch {
    return;
  }
  if (target.protocol !== "http:" && target.protocol !== "https:")
    return;

  event.preventDefault();
  if (currentDocumentUrl) {
    navigationHistory.push(currentDocumentUrl);
    updateBackButton();
  }
  urlInput.value = target.href;
  form.requestSubmit();
});

function render() {
  if (format.value === "text") {
    preview.classList.add("plain-text");
    preview.textContent = markdown.value;
    return;
  }

  preview.classList.remove("plain-text");
  preview.innerHTML = renderMarkdown(markdown.value);
  preview.querySelectorAll("img[src]").forEach(image => {
    let target;
    try {
      target = new URL(image.getAttribute("src"), currentDocumentUrl ?? location.href);
    } catch {
      return;
    }
    if (target.protocol === "http:" || target.protocol === "https:")
      image.src = `/asset?url=${encodeURIComponent(target.href)}`;
  });
}

function updateFormatUi() {
  const isMarkdown = format.value === "markdown";
  sourceTitle.textContent = isMarkdown ? "Markdown" : "Plain text";
  renderButton.hidden = !isMarkdown;
}

function updateBackButton() {
  backButton.disabled = navigationHistory.length === 0;
}

function renderMarkdown(source) {
  const lines = source.replaceAll("\r\n", "\n").split("\n");
  const output = [];
  let inCode = false;
  let code = [];
  let listOpen = false;

  const closeList = () => {
    if (listOpen) output.push("</ul>");
    listOpen = false;
  };

  for (let index = 0; index < lines.length; index++) {
    const line = lines[index];
    if (line.startsWith("```")) {
      closeList();
      if (inCode) {
        output.push(`<pre><code>${escapeHtml(code.join("\n"))}</code></pre>`);
        code = [];
      }
      inCode = !inCode;
      continue;
    }
    if (inCode) {
      code.push(line);
      continue;
    }

    if (isTableRow(line) && isTableSeparator(lines[index + 1] ?? "")) {
      closeList();
      const headers = tableCells(line);
      index += 2;
      const rows = [];
      while (index < lines.length && isTableRow(lines[index])) {
        rows.push(tableCells(lines[index]));
        index++;
      }
      index--;
      output.push("<div class=table-scroll><table><thead><tr>" + headers.map(cell => `<th>${inline(cell)}</th>`).join("") + "</tr></thead><tbody>");
      rows.forEach(row => output.push("<tr>" + row.map(cell => `<td>${inline(cell)}</td>`).join("") + "</tr>"));
      output.push("</tbody></table></div>");
      continue;
    }

    const heading = /^(#{1,6})\s+(.*)$/.exec(line);
    if (heading) {
      closeList();
      const level = heading[1].length;
      output.push(`<h${level}>${inline(heading[2])}</h${level}>`);
    } else if (line.startsWith("- ")) {
      if (!listOpen) output.push("<ul>");
      listOpen = true;
      output.push(`<li>${inline(line.slice(2))}</li>`);
    } else if (line.startsWith("> ")) {
      closeList();
      output.push(`<blockquote>${inline(line.slice(2))}</blockquote>`);
    } else if (line === "---") {
      closeList();
      output.push("<hr>");
    } else if (line.trim()) {
      closeList();
      output.push(`<p>${inline(line)}</p>`);
    } else {
      closeList();
    }
  }
  closeList();
  if (inCode) output.push(`<pre><code>${escapeHtml(code.join("\n"))}</code></pre>`);
  return output.join("\n");
}

function isTableRow(line) {
  return /^\s*\|.*\|\s*$/.test(line);
}

function isTableSeparator(line) {
  return /^\s*\|(?:\s*:?-{3,}:?\s*\|)+\s*$/.test(line);
}

function tableCells(line) {
  return line.trim().slice(1, -1).split(/(?<!\\)\|/).map(value => value.trim().replaceAll("\\|", "|"));
}

function inline(value) {
  return escapeHtml(value)
    .replace(/`([^`]+)`/g, "<code>$1</code>")
    .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
    .replace(/\*([^*]+)\*/g, "<em>$1</em>")
    .replace(/!\[([^\]]*)\]\(([^\s)]+)\)/g, '<img src="$2" alt="$1" loading="lazy">')
    .replace(/\[([^\]]+)\]\(([^\s)]+)\)/g, '<a href="$2" rel="noreferrer">$1</a>');
}

function escapeHtml(value) {
  return value.replace(/[&<>"']/g, character => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;"
  })[character]);
}

updateFormatUi();
render();
