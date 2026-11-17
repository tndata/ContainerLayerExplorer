// The start page: is Docker reachable, which images are there, and a way into the explorer for one of them.
import { $, esc, explorerUrl, debounce, fmtSize, getJson, hideMessage, listenToDocker, readSetting, setLive, shortId, showMessage } from "./common.js";

const state = {
  images: [],          // one entry per tag, newest first: { tag, id, size, created }
  selected: readSetting("image"),
};

async function refresh() {
  let status;
  try {
    status = await getJson("/api/status");
  } catch (e) {
    showDockerProblem(e.message);
    return;
  }
  if (!status.connected) {
    showDockerProblem(status.error);
    return;
  }

  showDockerOk(status);
  try {
    state.images = tagsOf(await getJson("/api/images"));
  } catch (e) {
    showMessage(e.message, { error: true });
    return;
  }
  hideMessage();
  chooseSelected();
  renderImages();
}

/** The server lists images with their tags; the page lists tags, because a tag is what you build and rebuild. */
function tagsOf(images) {
  return images.flatMap((img) => img.tags.map((tag) => ({ tag, id: img.id, size: img.size, created: img.created })));
}

/** Keep the chosen tag while it exists; otherwise the last one explored; otherwise the newest. */
function chooseSelected() {
  const tags = state.images.map((i) => i.tag);
  if (tags.includes(state.selected)) return;
  const remembered = readSetting("image");
  state.selected = tags.includes(remembered) ? remembered : tags[0] ?? null;
}

// ---------- the Docker card ----------

function showDockerOk(status) {
  const text = $("docker-status");
  text.className = "docker-status ok";
  const endpoint = document.createElement("code");   // fixed-width font for the technical part
  endpoint.textContent = status.endpoint;
  text.replaceChildren(`Docker is running (engine version ${status.version}). Connected through `, endpoint, ".");
  $("docker-note").hidden = true;
}

function showDockerProblem(message) {
  const text = $("docker-status");
  text.className = "docker-status problem";
  text.textContent = `${message} This page updates by itself when Docker is back.`;
  $("docker-note").hidden = false;   // what the tool supports matters only when it cannot reach Docker
  $("images-card").hidden = true;
  hideMessage();
}

// ---------- the image list ----------

function renderImages() {
  $("images-card").hidden = false;
  const empty = state.images.length === 0;
  $("images-empty").hidden = !empty;
  $("images").hidden = empty;

  $("images").querySelector("tbody").innerHTML = state.images.map(rowHtml).join("");
  for (const tr of $("images").querySelectorAll("tbody tr")) {
    tr.addEventListener("click", () => select(tr.dataset.tag));
    tr.addEventListener("dblclick", () => { location.href = explorerUrl(tr.dataset.tag); });
  }
  updateSelection();
}

/** 2026-11-17 13:39:26 in local time: the same everywhere, unlike 17/11/2026 or 11/17/2026. */
function fmtCreated(iso) {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "";
  const two = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${two(d.getMonth() + 1)}-${two(d.getDate())} ` +
    `${two(d.getHours())}:${two(d.getMinutes())}:${two(d.getSeconds())}`;
}

function rowHtml(image) {
  const created = fmtCreated(image.created);
  return `<tr data-tag="${esc(image.tag)}" tabindex="0">` +
    `<td class="tag"><code>${esc(image.tag)}</code></td>` +
    `<td><code>${shortId(image.id)}</code></td>` +
    `<td class="num">${fmtSize(image.size)}</td>` +
    `<td>${esc(created)}</td>` +
    `<td><a class="button" href="${esc(explorerUrl(image.tag))}">Explore</a></td></tr>`;
}

function select(tag) {
  state.selected = tag;
  updateSelection();
}

function updateSelection() {
  for (const tr of $("images").querySelectorAll("tbody tr")) tr.classList.toggle("selected", tr.dataset.tag === state.selected);
  const explore = $("explore");
  const enabled = state.selected !== null;
  explore.href = enabled ? explorerUrl(state.selected) : "#";
  explore.setAttribute("aria-disabled", String(!enabled));
}

function neighbour(row, key) {
  if (key === "ArrowDown") return row.nextElementSibling;
  if (key === "ArrowUp") return row.previousElementSibling;
  return null;
}

// Enter on a row explores it; arrows move between rows.
document.addEventListener("keydown", (e) => {
  const row = document.activeElement?.closest?.("#images tbody tr");
  if (!row) return;
  if (e.key === "Enter") location.href = explorerUrl(row.dataset.tag);
  const next = neighbour(row, e.key);
  if (next) {
    e.preventDefault();
    next.focus();
    select(next.dataset.tag);
  }
});

// ---------- go ----------

try {
  await refresh();
  const refreshSoon = debounce(refresh, 400);
  listenToDocker(() => refreshSoon(), setLive);
} catch (e) {
  showMessage(`Something went wrong in the page: ${e.message}`, { error: true });
}
