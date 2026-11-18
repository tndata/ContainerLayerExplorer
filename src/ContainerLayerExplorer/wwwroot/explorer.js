// Container Layer Explorer: the explorer page. It shows one image, the tag in the address (explorer.html?image=name),
// and follows rebuilds of it. Two views: the Matrix (here, shown first) and the Stack (drawn by stack.js).
// Talks only to /api (see Api/Endpoints.cs). No framework.
// Loaded as a module (explorer.html), so it is strict already and can use top-level await.
import { $, debounce, esc, fmtSize, getJson, hideMessage, listenToDocker, setLive, shortCmd, shortId, showMessage, writeSetting } from "./common.js";
import { renderStackView, selectStackImage } from "./stack.js";

// Collapse rules. A folder starts collapsed when it sits directly under /, has more
// than COLLAPSE_OVER changed paths below it, is in ALWAYS_COLLAPSE, or only the first (base) layer touches it.
// Two exceptions stay open: the working directory (WORKDIR) and the folders on the way to it, and every folder of an
// image with SMALL_IMAGE paths or fewer in total.
const COLLAPSE_OVER = 20;
const SMALL_IMAGE = 20;
const ALWAYS_COLLAPSE = new Set(["/proc", "/sys", "/dev", "/usr/share/doc", "/var/cache", "/usr/lib", "/lib"]);
const MAX_ROWS = 3000;
const MAX_INDENT_LEVELS = 8;   // deeper folders are indented no further, so a deep tree does not run off the screen

const state = {
  name: new URLSearchParams(location.search).get("image"),
  view: "matrix",           // "matrix" or "stack"; an image always opens in the Matrix view
  current: null,            // { id, name, model }
  tags: [],                 // every tag of the image shown, from /api/images
  previous: new Map(),      // image name -> diff IDs of the build shown before (for the NEW badges)
  open: new Map(),          // folder path -> true (expanded) / false (collapsed), overrides the rules
  filter: "",
  selected: null,           // { path, layer }
  rows: null, tree: null,
  problem: null,            // a message about Docker or the server, shown in red until it is gone
  gone: false,              // the tag was removed or untagged; the last build seen stays on screen
  loading: false,
};

// ---------- startup and live updates ----------

async function init() {
  if (!state.name) {
    showMessage("No image was chosen.", { backLink: true });
    return;
  }
  document.title = `${state.name} - Container Layer Explorer`;
  $("image-name").textContent = state.name;
  $("refresh").addEventListener("click", () => refresh(true));
  $("filter").addEventListener("input", (e) => {
    state.filter = e.target.value.trim();
    if (state.current && state.view === "matrix") renderMatrix();
  });
  $("view-stack").addEventListener("click", () => setView("stack"));
  $("view-matrix").addEventListener("click", () => setView("matrix"));
  $("panel-close").addEventListener("click", closePanel);
  document.addEventListener("keydown", (e) => {
    if (e.key !== "Escape") return;
    closePanel();
    if (state.view === "stack") selectStackImage();
    document.querySelector("details.help").open = false;   // Escape also closes the Help window
  });
  await refresh(false);
  const refreshSoon = debounce(() => refresh(false), 400);
  listenToDocker(() => refreshSoon(), setLive);
}

/** Asks the server what Docker has, and loads the image when it is new or changed (or when force is true). */
async function refresh(force) {
  let images;
  try {
    const status = await getJson("/api/status");
    if (!status.connected) { setProblem(status.error); return; }
    images = await getJson("/api/images");
  } catch (e) {
    setProblem(e.message);
    return;
  }
  state.problem = null;
  const found = images.find((i) => i.tags.includes(state.name));
  state.gone = !found;
  if (!found) {
    updateMessage();
    return;
  }
  const tagsChanged = found.tags.join(" ") !== state.tags.join(" ");
  state.tags = found.tags;
  if (force || !state.current || state.current.id !== found.id) await selectImage();
  else {
    updateMessage();
    if (tagsChanged && state.view === "stack") render();   // the Stack header lists the tags
  }
}

function setProblem(message) {
  state.problem = message;
  updateMessage();
}

/** One place decides what the message bar says, from what is true now. */
function updateMessage() {
  if (state.problem) { showMessage(state.problem, { error: true }); return; }
  if (state.gone && !state.current) { showMessage(`There is no image tagged ${state.name}.`, { error: true, backLink: true }); return; }
  if (state.gone) { showMessage(`${state.name} no longer exists (removed or untagged). This is the last build seen.`, { backLink: true }); return; }
  if (state.loading) { showMessage(`Loading ${state.name} ...`, { busy: true }); return; }
  if (state.current?.model.layers.length === 0) { showMessage("This image is empty (it has no layers)."); return; }
  hideMessage();
}

async function selectImage() {
  state.loading = true;
  updateMessage();
  let result;
  try {
    result = await getJson(`/api/image?name=${encodeURIComponent(state.name)}`);
  } catch (e) {
    state.loading = false;
    setProblem(e.message);
    return;
  }
  state.loading = false;

  const before = state.current;
  if (before && before.id !== result.id) {
    state.previous.set(state.name, before.model.layers.map((l) => l.diffId));
  }
  if (!before || before.id !== result.id) state.selected = null;
  state.current = result;
  writeSetting("image", state.name);
  state.rows = buildRows(result.model);
  state.tree = buildTree(state.rows);
  state.problem = null;
  updateMessage();
  render();
  await reopenSelected();
}

/** After a reload, show the same cell again when it is still there. */
async function reopenSelected() {
  if (!state.selected) return;
  const { path, layer } = state.selected;
  if (layer !== "result") { await showCell(path, layer); return; }
  const sees = state.rows.get(path)?.sees;
  if (sees) await showCell(path, sees.layer, true);
  else closePanel();
}

// ---------- data shaping ----------

/** One row per path; cells[layerIndex] = change. */
function buildRows(model) {
  const rows = new Map();
  for (const layer of model.layers) {
    for (const c of layer.changes) {
      let row = rows.get(c.path);
      if (!row) { row = { path: c.path, entry: c.entry, cells: [] }; rows.set(c.path, row); }
      row.cells[layer.index] = c;
      if (c.kind !== "Deleted") row.entry = c.entry;
    }
  }
  const sees = new Map(model.containerSees.map((v) => [v.path, v]));
  for (const row of rows.values()) {
    row.sees = sees.get(row.path);
    // A folder that only got new timestamps is noise; its files are what matter.
    const changes = row.cells.filter(Boolean);
    row.hidden = row.entry === "Directory" && changes.every((c) => c.kind === "Modified");
  }
  return rows;
}

/** Folder tree over the rows, with the number of rows below each folder. */
function buildTree(rows) {
  const root = { path: "/", children: new Map(), count: 0, total: 0 };
  for (const row of rows.values()) {
    if (row.hidden) continue;
    root.total++;
    const parts = row.path.split("/").slice(1);
    let node = root, path = "";
    parts.forEach((part, i) => {
      path += "/" + part;
      let child = node.children.get(part);
      if (!child) { child = { path, name: part, children: new Map(), count: 0 }; node.children.set(part, child); }
      if (i < parts.length - 1) child.count++;
      node = child;
    });
    node.row = row;
  }
  return root;
}

function isOpen(node) {
  if (state.open.has(node.path)) return state.open.get(node.path);
  if (state.tree.total <= SMALL_IMAGE) return true;
  if (isOnWayToWorkingDir(node.path)) return true;
  if (isTopLevel(node.path)) return false;
  if (ALWAYS_COLLAPSE.has(node.path)) return false;
  if (onlyBaseLayer(node)) return false;
  return node.count <= COLLAPSE_OVER;
}

/** A folder directly under / such as /usr or /app. */
function isTopLevel(path) {
  return path.lastIndexOf("/") === 0;
}

/** The working directory (WORKDIR) is where your own files are, so it and its parents start open. */
function isOnWayToWorkingDir(path) {
  const workingDir = state.current.model.config.workingDir;
  return !!workingDir && (workingDir === path || workingDir.startsWith(path + "/"));
}

/** True when nothing below this folder changes after the first layer (typically the base image).
 *  Also applies to a one-layer image such as plain FROM busybox. Your own folders on scratch stay open
 *  through NEVER_COLLAPSE and WORKDIR. */
function onlyBaseLayer(node) {
  return subtreeRows(node).every((r) => r.cells.every((c, i) => !c || i === 0));
}

/** Display lines: plain rows, open folders, and collapsed folder summaries. */
function displayLines() {
  if (state.filter) {
    const f = state.filter.toLowerCase();
    return [...state.rows.values()]
      .filter((r) => !r.hidden && r.path.toLowerCase().includes(f))
      .sort((a, b) => cmp(a.path, b.path))
      .map((row) => ({ type: "row", row, depth: 0 }));
  }
  const out = [];
  const walk = (node, depth) => {
    for (const child of [...node.children.values()].sort((a, b) => cmp(a.name, b.name))) {
      if (child.count === 0) { out.push({ type: "row", row: child.row, depth }); continue; }
      if (isOpen(child)) { out.push({ type: "folder", node: child, depth }); walk(child, depth + 1); }
      else out.push({ type: "summary", node: child, depth });
    }
  };
  walk(state.tree, 0);
  return out;
}

function subtreeRows(node) {
  const rows = [];
  const walk = (n) => {
    if (n.row) rows.push(n.row);
    n.children.forEach(walk);
  };
  walk(node);
  return rows;
}

// ---------- rendering ----------

/** Shows the chosen view. The Stack is drawn by stack.js; the Matrix, its filter and its side panel live here. */
function render() {
  const { model } = state.current;
  const stack = state.view === "stack";
  $("view-stack").setAttribute("aria-pressed", String(stack));
  $("view-matrix").setAttribute("aria-pressed", String(!stack));
  $("filter").hidden = stack;
  $("summary").hidden = stack;
  $("stack-view").hidden = !stack;
  if (stack) {
    $("matrix-wrap").hidden = true;
    renderStackView(state.current, {
      tags: state.tags,
      previous: state.previous.get(state.current.name),
    });
    return;
  }
  renderImageInfo(model);
  renderMatrix();
}

function setView(view) {
  if (state.view === view) return;
  state.view = view;
  closePanel();   // the side panel belongs to the Matrix
  if (state.current) render();
}

/** Above the matrix: only the image ID (it changes when a rebuild makes a new image) and a warning for a cut-off image.
 *  Sizes are not the point of the matrix: the image and layer sizes are in the Stack view. */
function renderImageInfo(model) {
  $("image-info").innerHTML =
    `<span class="imageid" title="${esc(state.current.id)}">Image ID <code>${shortId(state.current.id)}</code></span>` +
    (model.truncated ? ` <span class="warning">Too many files: only the first part is shown.</span>` : "");
}

function renderMatrix() {
  const { model } = state.current;
  const wrap = $("matrix-wrap");
  if (model.layers.length === 0) {
    wrap.hidden = true;   // updateMessage() says why
    return;
  }
  wrap.hidden = false;
  const table = $("matrix");
  const prev = state.previous.get(state.current.name);
  const prevSet = prev ? new Set(prev) : null;

  let head = `<thead><tr><th class="pathcol">Path</th>`;
  for (const layer of model.layers) {
    // Only changed layers get a badge: no badge means "same as the previous build".
    const isNew = prevSet && !prevSet.has(layer.diffId);
    const badge = isNew ? `<span class="badge new">new</span>` : "";
    const tip = [layer.createdBy ?? "", `Layer digest: ${layer.diffId}`, buildNote(layer, prevSet),
      "Click for the details."].filter(Boolean).join("&#10;");
    head += `<th class="layerhead clickable" data-layer="${layer.index}" tabindex="0" title="${esc(tip).replaceAll("&amp;#10;", "&#10;")}">` +
      `<div><span class="n">${layer.index + 1}</span>${badge}</div>` +
      `<div class="cmdline">${esc(shortCmd(layer.createdBy ?? ""))}</div>` +
      `<div class="digestline"><code class="digest">${shortId(layer.diffId)}</code></div></th>`;
  }
  head += `<th class="seescol" title="What a container started from this image sees. ✓ = the container gets this file.">Result</th></tr></thead>`;

  const lines = displayLines();
  let body = "<tbody>";
  for (const line of lines.slice(0, MAX_ROWS)) body += renderLine(line, model);
  if (lines.length > MAX_ROWS) body += `<tr><td class="pathcell" colspan="${model.layers.length + 2}">${lines.length - MAX_ROWS} more rows. Use the filter.</td></tr>`;
  if (lines.length === 0) body += `<tr><td class="pathcell" colspan="${model.layers.length + 2}">No paths match.</td></tr>`;
  table.classList.toggle("filtered", !!state.filter);   // a filtered list shows full paths, so the first column gets more room
  table.innerHTML = head + body + "</tbody>";

  table.querySelectorAll("th.layerhead").forEach((th) => {
    th.addEventListener("click", () => showLayer(Number(th.dataset.layer)));
    th.addEventListener("keydown", (e) => { if (e.key === "Enter") showLayer(Number(th.dataset.layer)); });
  });
  table.querySelectorAll("td.c").forEach((td) =>
    td.addEventListener("click", () => showCell(td.dataset.path, Number(td.dataset.layer))));
  table.querySelectorAll("td.sees.click").forEach((td) =>
    td.addEventListener("click", () => showCell(td.dataset.path, state.rows.get(td.dataset.path).sees.layer, true)));
  table.querySelectorAll(".toggle").forEach((t) =>
    t.addEventListener("click", () => { state.open.set(t.dataset.path, t.dataset.open !== "true"); renderMatrix(); }));
  markSelected();
}

function renderLine(line, model) {
  const indent = `style="padding-left:${8 + Math.min(line.depth, MAX_INDENT_LEVELS) * 18}px"`;
  return line.type === "summary" ? renderSummaryLine(line, model, indent) : renderRowLine(line, model, indent);
}

/** A collapsed folder: one line with a count of what changed below it in each layer. */
function renderSummaryLine(line, model, indent) {
  const rows = subtreeRows(line.node);
  let html = `<tr><td class="pathcell" ${indent}><span class="toggle" data-path="${esc(line.node.path)}" data-open="false">&#9656; ` +
    `<span class="path">${esc(shownPath(line.node.path, line.depth))}/</span></span> <span class="count">(${rows.length} paths)</span></td>`;
  for (const layer of model.layers) html += summaryLayerCell(rows, layer.index);
  return html + summarySeesCell(rows);
}

/** How many paths below a collapsed folder this layer adds, changes and deletes. */
function summaryLayerCell(rows, layerIndex) {
  const n = { Added: 0, Modified: 0, Deleted: 0 };
  for (const r of rows) {
    const c = r.cells[layerIndex];
    if (c && !(r.entry === "Directory" && c.kind === "Modified")) n[c.kind]++;
  }
  const parts = [];
  if (n.Added) parts.push(`<span class="a">+${n.Added}</span>`);
  if (n.Modified) parts.push(`<span class="m">~${n.Modified}</span>`);
  if (n.Deleted) parts.push(`<span class="d">&minus;${n.Deleted}</span>`);
  return `<td class="agg${parts.length ? " has" : ""}">${parts.join(" ")}</td>`;
}

function summarySeesCell(rows) {
  const seen = rows.filter((r) => r.sees && r.sees.entry !== "Directory").length;
  // Number = files the container gets; a folder holding only folders still exists, so it gets a plain tick.
  const any = seen || rows.some((r) => r.sees);
  let content = "";
  if (seen) content = `&#10003; <span class="count">${seen}</span>`;
  else if (any) content = "&#10003;";
  return `<td class="sees${seen ? "" : " dir"}">${content}</td></tr>`;
}

/** A path, or an open folder: the path, one cell per layer, and the Result cell. */
function renderRowLine(line, model, indent) {
  const isFolder = line.type === "folder";
  const row = isFolder ? line.node.row : line.row;
  const path = isFolder ? line.node.path : row.path;
  const isDir = row?.entry === "Directory" || isFolder;
  let html = `<tr data-path="${esc(path)}"><td class="pathcell${isDir ? " dirrow" : ""}" ${indent} title="${esc(path)}">${rowLabel(isFolder, row, path, isDir, line.depth)}</td>`;
  for (const layer of model.layers) html += rowLayerCell(row, path, isDir, layer.index);
  return html + rowSeesCell(line, row, path, isDir);
}

/** What the first column shows: the full path at the top level and in a filtered list, otherwise only the name,
 *  because the indentation already shows where it sits. The full path is in the tooltip and in the panel. */
function shownPath(path, depth) {
  return depth === 0 ? path : path.slice(path.lastIndexOf("/") + 1);
}

function rowLabel(isFolder, row, path, isDir, depth) {
  const shown = esc(shownPath(path, depth));
  if (isFolder) {
    return `<span class="toggle" data-path="${esc(path)}" data-open="true">&#9662; <span class="path">${shown}/</span></span>`;
  }
  const linkNote = row.entry === "Symlink" ? ' <span class="count">link</span>' : "";
  return `<span class="path">${shown}${isDir ? "/" : ""}</span>${linkNote}`;
}

function rowLayerCell(row, path, isDir, layerIndex) {
  // A folder is "modified" whenever something inside it changes: true, but noise. Show only A and D.
  let c = row && !row.hidden ? row.cells[layerIndex] : null;
  if (c && isDir && c.kind === "Modified") c = null;
  if (!c) return "<td></td>";
  const dirClass = isDir ? " dir" : "";
  return `<td class="c ${c.kind}${dirClass}" data-path="${esc(path)}" data-layer="${layerIndex}">${c.kind[0]}</td>`;
}

function rowSeesCell(line, row, path, isDir) {
  // A folder only implied by the files inside it has no entry of its own: present if anything inside is.
  const sees = row?.sees ?? (line.type === "folder" && subtreeRows(line.node).some((r) => r.sees));
  const clickable = !isDir && row?.sees;
  const dataPath = clickable ? ` data-path="${esc(path)}"` : "";
  return `<td class="sees${isDir ? " dir" : ""}${clickable ? " click" : ""}"${dataPath}>${sees ? "&#10003;" : ""}</td></tr>`;
}

// ---------- detail panel ----------

/** What a layer header says about the previous build, or "" when there is no previous build. */
function buildNote(layer, prevSet) {
  if (!prevSet) return "";
  return prevSet.has(layer.diffId)
    ? "Same as in your previous build (docker build shows CACHED)."
    : "NEW: not in your previous build.";
}

/** The steps without a layer (ENV, ARG, LABEL, CMD and so on) that come before the first layer. */
function stepsWithoutLayerBeforeFirst(history) {
  const steps = [];
  for (const s of history) {
    if (s.layerIndex !== null) break;
    steps.push(s);
  }
  return steps;
}

/** The steps without a layer right after a layer, up to the next layer. */
function stepsWithoutLayerAfter(history, layerIndex) {
  const at = history.findIndex((s) => s.layerIndex === layerIndex);
  if (at < 0) return [];
  const steps = [];
  for (let i = at + 1; i < history.length && history[i].layerIndex === null; i++) steps.push(history[i]);
  return steps;
}

function stepList(label, steps) {
  if (steps.length === 0) return "";
  return `<p class="what">${label} (they change the image settings, not its files):</p><ul class="stepslist">` +
    steps.map((s) => `<li><code>${esc(shortCmd(s.createdBy ?? ""))}</code></li>`).join("") + "</ul>";
}

/** Click on a layer header: the whole command, the size and digest, and the steps that made no layer. */
function showLayer(index) {
  const { model } = state.current;
  const layer = model.layers[index];
  if (!layer) return;
  state.selected = null;
  markSelected();
  document.querySelector(`#matrix th.layerhead[data-layer="${index}"]`)?.classList.add("selcol");
  const prev = state.previous.get(state.current.name);
  const note = buildNote(layer, prev ? new Set(prev) : null);
  const first = index === 0 ? stepsWithoutLayerBeforeFirst(model.history) : [];

  $("panel-body").innerHTML = `<h2>Layer ${index + 1}</h2>` +
    `<div class="meta">Digest <code>${esc(layer.diffId)}</code></div>` +
    (note ? `<p class="what">${esc(note)}</p>` : "") +
    stepList("Steps before this layer that make no layer of their own", first) +
    `<p class="what">The build step:</p><pre>${esc(layer.createdBy ?? "(not recorded)")}</pre>` +
    stepList("Steps after this layer that make no layer of their own", stepsWithoutLayerAfter(model.history, index));
  $("panel").hidden = false;
}

// fromResult: opened from the Result column, so show only the version the container gets.
async function showCell(path, layer, fromResult = false) {
  const row = state.rows.get(path);
  const change = row?.cells[layer];
  if (!change) { closePanel(); return; }
  state.selected = { path, layer: fromResult ? "result" : layer };
  markSelected();
  const body = $("panel-body");
  $("panel").hidden = false;
  const n = layer + 1;
  const earlier = lastVersionBefore(row, layer);
  const header = `<h2 class="path">${esc(path)}</h2><div class="meta">Layer ${n}: ${esc(shortCmd(state.current.model.layers[layer].createdBy ?? ""))}</div>`;

  const view = describeChange(row, change, { n, layer, earlier, fromResult });
  body.innerHTML = header + view.html;
  if (view.loadsContent) await loadContent(path, layer);
  body.querySelectorAll("[data-show]").forEach((b) => b.addEventListener("click", () => showCell(path, Number(b.dataset.show))));
}

/** What the panel says about one change, and whether the file's content has to be fetched and shown below it. */
function describeChange(row, change, { n, layer, earlier, fromResult }) {
  if (change.kind === "Deleted") return { html: deletedHtml(change, n, earlier), loadsContent: false };
  if (row.entry === "Directory") {
    return { html: `<p class="what">Folder ${change.kind === "Added" ? "created" : "changed"} in layer ${n}.</p>`, loadsContent: false };
  }
  if (row.entry === "Symlink") {
    return { html: `<p class="what">Symbolic link to <code>${esc(change.linkTarget ?? "?")}</code>.</p>`, loadsContent: false };
  }
  const loading = `<div data-content="${layer}">Loading...</div>`;
  if (fromResult) {
    return { html: `<p class="what">This is the version the container sees. It comes from layer ${n}.</p>` + loading, loadsContent: true };
  }
  // A cell shows what that layer stores; the older version is one click away.
  let html = change.kind === "Added"
    ? `<p class="what">Added in layer ${n}.</p>`
    : `<p class="what">Changed in layer ${n}. The layer stores the <b>whole</b> new file.</p>`;
  html += loading;
  if (change.kind === "Modified" && earlier !== null) {
    html += `<p class="what">The old version is still stored in layer ${earlier + 1}. ${showItButton(earlier)}</p>`;
  }
  return { html, loadsContent: true };
}

function deletedHtml(change, n, earlier) {
  let html = change.whiteoutMarker
    ? `<p class="what">Deleted in layer ${n}. The layer only holds a marker file, <code>${esc(change.whiteoutMarker)}</code>, that hides it.</p>`
    : `<p class="what">Hidden in layer ${n}. ${esc(change.note ?? "")}</p>`;
  if (earlier !== null) {
    html += `<p class="what">The file is <b>not</b> gone from the image: its data is still stored in layer ${earlier + 1}. ${showItButton(earlier)}</p>`;
  }
  return html;
}

function showItButton(layerIndex) {
  return `<button type="button" class="link" data-show="${layerIndex}">Show it</button>`;
}

async function loadContent(path, layer) {
  const target = document.querySelector(`[data-content="${layer}"]`);
  const c = await getJson(`/api/file?id=${encodeURIComponent(state.current.id)}&layer=${layer}&path=${encodeURIComponent(path)}`);
  if (!target) return;
  if (!c) { target.textContent = "Could not read the file."; return; }
  const meta = `<div class="meta">${fmtSize(c.size)}${c.truncated ? ", only the start is shown" : ""}</div>`;
  target.innerHTML = c.text !== null && c.text !== undefined
    ? meta + `<pre>${esc(c.text) || "<i>(empty)</i>"}</pre>`
    : meta + `<p>${esc(c.message ?? "")}</p>`;
}

/** The newest layer below `layer` that wrote this path, or null. */
function lastVersionBefore(row, layer) {
  for (let i = layer - 1; i >= 0; i--) {
    const c = row.cells[i];
    if (c && c.kind !== "Deleted") return i;
    if (c?.kind === "Deleted") return null;
  }
  return null;
}

function closePanel() {
  $("panel").hidden = true;
  state.selected = null;
  markSelected();
}

function markSelected() {
  document.querySelectorAll("tr.sel").forEach((tr) => tr.classList.remove("sel"));
  document.querySelectorAll("td.selcell").forEach((td) => td.classList.remove("selcell"));
  document.querySelectorAll("th.selcol").forEach((th) => th.classList.remove("selcol"));
  if (!state.selected) return;
  document.querySelectorAll("#matrix tr[data-path]").forEach((tr) => {
    if (tr.dataset.path !== state.selected.path) return;
    tr.classList.add("sel");
    const cell = state.selected.layer === "result" ? "td.sees" : `td.c[data-layer="${state.selected.layer}"]`;
    tr.querySelector(cell)?.classList.add("selcell");
  });
}

// ---------- helpers ----------

function cmp(a, b) {
  if (a < b) return -1;
  if (a > b) return 1;
  return 0;
}

try {
  await init();
} catch (e) {
  showMessage(`Something went wrong in the page: ${e.message}`, { error: true });
}
