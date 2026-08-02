const form = document.querySelector("#convert-form");
const html = document.querySelector("#html");
const markdown = document.querySelector("#markdown");
const status = document.querySelector("#status");
const submit = form.querySelector("button[type=submit]");

form.addEventListener("submit", async event => {
  event.preventDefault();
  submit.disabled = true;
  status.textContent = "Folding HTML …";

  try {
    const response = await fetch("/markdown", {
      method: "POST",
      headers: { "Content-Type": "text/html; charset=utf-8" },
      body: html.value
    });
    if (!response.ok) throw new Error(`${response.status} ${await response.text()}`);
    markdown.value = await response.text();
    status.textContent = `${markdown.value.length.toLocaleString()} Markdown characters.`;
  } catch (error) {
    markdown.value = "";
    status.textContent = `Conversion failed: ${error.message}`;
  } finally {
    submit.disabled = false;
  }
});
