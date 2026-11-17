// Container Layer Explorer page. Talks only to /api (see Api/Endpoints.cs). No framework.
"use strict";

// Collapse rules (ADR 0004). A folder starts collapsed when it has more than COLLAPSE_OVER changed
// paths below it, is in ALWAYS_COLLAPSE, or only the first (base) layer touches it.
const COLLAPSE_OVER = 20;
const ALWAYS_COLLAPSE = ["/proc", "/sys", "/dev", "/usr/share/doc", "/var/cache", "/usr/lib", "/lib"];
const NEVER_COLLAPSE = ["/app", "/home", "/src", "/root"];
const MAX_ROWS = 3000;

const state = {
  images: [],
  name: readSetting("image"),
  current: null,            // { id, name, model }
  previous: new Map(),      // image name -> diff IDs of the build shown before (for the NEW badges)
  open: new Map(),          // folder path -> true (expanded) / false (collapsed), overrides the rules
  filter: "",
  selected: null,           // { path, layer }
  rows: null, tree: null,
};

const $ = (id) => document.getElementById(id);

// ---------- startup and live updates ----------

async function init() {
  $("image-select").addEventListener("change", (e) => selectImage(e.target.value));
  $("refresh").addEventListener("click", () => refresh(true));
  $("filter").addEventListener("input", (e) => { state.filter = e.target.value.trim(); renderMatrix(); });
  $("panel-close").addEventListener("click", closePanel);
  document.addEventListener("keydown", (e) => { if (e.key === "Escape") closePanel(); });
  document.querySelectorAll(".copy").forEach((b) => b.addEventListener("click", () => copy(b)));
  await refresh(false);
  listen();
}

async function refresh(force) {
  const status = await getJson("/api/status");
  if (!status || !status.connected) {
    showMessage(status?.error ?? "Cannot reach the Container Layer Explorer server.", true);
    return;
  }
  state.images = (await getJson("/api/images")) ?? [];
  renderPicker();
  const tags = state.images.flatMap((i) => i.tags);
  if (!state.name || !tags.includes(state.name)) state.name = tags.includes("demo:latest") ? "demo:latest" : tags[0];
  if (!state.name) { showMessage("No tagged images yet. Build one: docker build -t demo ."); return; }
  $("image-select").value = state.name;
  const id = state.images.find((i) => i.tags.includes(state.name))?.id;
  if (force || !state.current || state.current.id !== id || state.current.name !== state.name) await selectImage(state.name);
}

function listen() {
  let timer;
  const source = new EventSource("/api/events");
  source.addEventListener("image", (e) => {
    const action = JSON.parse(e.data).action;
    if (action === "disconnected") { $("live").classList.remove("on"); return; }   // Docker went away; the server keeps trying
    $("live").classList.add("on");
    if (action === "connected") return;
    clearTimeout(timer);                       // a build fires several events; react once
    timer = setTimeout(() => refresh(false), 400);
  });
  source.onerror = () => $("live").classList.remove("on");   // EventSource reconnects by itself
}

async function selectImage(name) {
  state.name = name;
  writeSetting("image", name);
  showMessage(`Loading ${name} ...`);
  const result = await getJson(`/api/image?name=${encodeURIComponent(name)}`);
  if (!result) { showMessage(`Could not load ${name}.`, true); return; }

  const before = state.current;
  if (before && before.name === name && before.id !== result.id) {
    state.previous.set(name, before.model.layers.map((l) => l.diffId));
  }
  if (!before || before.id !== result.id) state.selected = null;
  state.current = result;
  state.rows = buildRows(result.model);
  state.tree = buildTree(state.rows);
  hideMessage();
  render();
  if (state.selected?.layer === "result") {
    const sees = state.rows.get(state.selected.path)?.sees;
    if (sees) showCell(state.selected.path, sees.layer, true); else closePanel();
  } else if (state.selected) showCell(state.selected.path, state.selected.layer);
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
  const root = { path: "/", children: new Map(), count: 0 };
  for (const row of rows.values()) {
    if (row.hidden) continue;
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
  if (NEVER_COLLAPSE.some((p) => node.path === p || node.path.startsWith(p + "/"))) return true;
  if (state.current.model.config.workingDir === node.path) return true;
  if (ALWAYS_COLLAPSE.includes(node.path)) return false;
  if (onlyBaseLayer(node)) return false;
  return node.count <= COLLAPSE_OVER;
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
  const walk = (n) => { if (n.row) rows.push(n.row); n.children.forEach(walk); };
  walk(node);
  return rows;
}

// ---------- rendering ----------

function render() {
  const { model } = state.current;
  $("summary").hidden = false;
  renderSizes(model);
  renderSteps(model);
  renderMatrix();
}

function renderPicker() {
  const select = $("image-select");
  select.innerHTML = "";
  for (const img of state.images) {
    for (const tag of img.tags) select.add(new Option(tag, tag));
  }
}

function renderSizes(model) {
  const hidden = model.totalSize - model.visibleSize;
  $("sizes").innerHTML =
    `<span class="imageid" title="${esc(state.current.id)}">Image ID <code>${shortId(state.current.id)}</code></span> ` +
    `Files stored in the image: <b>${fmtSize(model.totalSize)}</b> in ${model.layers.length} layer${model.layers.length === 1 ? "" : "s"}. ` +
    `The container sees: <b>${fmtSize(model.visibleSize)}</b>.` +
    (hidden > 0 ? ` <span class="hidden-size">Stored but hidden or overwritten: <b>${fmtSize(hidden)}</b>.</span>` : "") +
    (model.truncated ? ` <span class="hidden-size">Too many files: only the first part is shown.</span>` : "");
}

function renderSteps(model) {
  const ol = $("steps");
  ol.innerHTML = "";
  for (const step of model.history) {
    const li = document.createElement("li");
    li.title = step.createdBy;
    if (step.layerIndex === null) {
      li.className = "empty";
      li.textContent = `${shortCmd(step.createdBy)} (no layer)`;
    } else {
      li.innerHTML = `<span class="num">${step.layerIndex + 1}</span>`;
      li.append(shortCmd(step.createdBy));
    }
    ol.append(li);
  }
}

function renderMatrix() {
  const { model } = state.current;
  const wrap = $("matrix-wrap");
  if (model.layers.length === 0) {
    wrap.hidden = true;
    showMessage("This image is empty (it has no layers).");
    return;
  }
  hideMessage();
  wrap.hidden = false;
  const table = $("matrix");
  const prev = state.previous.get(state.current.name);
  const prevSet = prev ? new Set(prev) : null;

  let head = `<thead><tr><th class="pathcol">Path</th>`;
  for (const layer of model.layers) {
    // Only changed layers get a badge: no badge means "same as the previous build".
    const isNew = prevSet && !prevSet.has(layer.diffId);
    const badge = isNew ? `<span class="badge new">new</span>` : "";
    const tip = [layer.createdBy ?? "", `Layer digest: ${layer.diffId}`,
      isNew ? "NEW: not in your previous build." : prevSet ? "Same as in your previous build (docker build shows CACHED)." : "",
      `Size: file bytes. docker history shows the space on disk, which is bigger.`].filter(Boolean).join("&#10;");
    head += `<th class="layerhead" title="${esc(tip).replace(/&amp;#10;/g, "&#10;")}">` +
      `<div><span class="n">${layer.index + 1}</span>${badge}</div>` +
      `<div class="cmdline">${esc(shortCmd(layer.createdBy ?? ""))}</div>` +
      `<div class="size">${fmtSize(layer.size)} <code class="digest">${shortId(layer.diffId)}</code></div></th>`;
  }
  head += `<th class="seescol" title="What a container started from this image sees. ✓ = the container gets this file.">Result</th></tr></thead>`;

  const lines = displayLines();
  let body = "<tbody>";
  for (const line of lines.slice(0, MAX_ROWS)) body += renderLine(line, model);
  if (lines.length > MAX_ROWS) body += `<tr><td class="pathcell" colspan="${model.layers.length + 2}">${lines.length - MAX_ROWS} more rows. Use the filter.</td></tr>`;
  if (lines.length === 0) body += `<tr><td class="pathcell" colspan="${model.layers.length + 2}">No paths match.</td></tr>`;
  table.innerHTML = head + body + "</tbody>";

  table.querySelectorAll("td.c").forEach((td) =>
    td.addEventListener("click", () => showCell(td.dataset.path, Number(td.dataset.layer))));
  table.querySelectorAll("td.sees.click").forEach((td) =>
    td.addEventListener("click", () => showCell(td.dataset.path, state.rows.get(td.dataset.path).sees.layer, true)));
  table.querySelectorAll(".toggle").forEach((t) =>
    t.addEventListener("click", () => { state.open.set(t.dataset.path, t.dataset.open !== "true"); renderMatrix(); }));
  markSelected();
}

function renderLine(line, model) {
  const indent = `style="padding-left:${8 + line.depth * 18}px"`;
  if (line.type === "summary") {
    const rows = subtreeRows(line.node);
    let html = `<tr><td class="pathcell" ${indent}><span class="toggle" data-path="${esc(line.node.path)}" data-open="false">&#9656; ` +
      `<span class="path">${esc(line.node.path)}/</span></span> <span class="count">(${rows.length} paths)</span></td>`;
    for (const layer of model.layers) {
      const n = { Added: 0, Modified: 0, Deleted: 0 };
      for (const r of rows) { const c = r.cells[layer.index]; if (c && !(r.entry === "Directory" && c.kind === "Modified")) n[c.kind]++; }
      const parts = [n.Added && `<span class="a">+${n.Added}</span>`, n.Modified && `<span class="m">~${n.Modified}</span>`, n.Deleted && `<span class="d">&minus;${n.Deleted}</span>`].filter(Boolean);
      html += `<td class="agg${parts.length ? " has" : ""}">${parts.join(" ")}</td>`;
    }
    const seen = rows.filter((r) => r.sees && r.sees.entry !== "Directory").length;
    // Number = files the container gets; a folder holding only folders still exists, so it gets a plain tick.
    const any = seen || rows.some((r) => r.sees);
    return html + `<td class="sees${seen ? "" : " dir"}">${seen ? `&#10003; <span class="count">${seen}</span>` : any ? "&#10003;" : ""}</td></tr>`;
  }

  const row = line.type === "folder" ? line.node.row : line.row;
  const path = line.type === "folder" ? line.node.path : row.path;
  const isDir = row?.entry === "Directory" || line.type === "folder";
  const label = line.type === "folder"
    ? `<span class="toggle" data-path="${esc(path)}" data-open="true">&#9662; <span class="path">${esc(path)}/</span></span>`
    : `<span class="path">${esc(path)}${isDir ? "/" : ""}</span>${row.entry === "Symlink" ? ` <span class="count">link</span>` : ""}`;
  let html = `<tr data-path="${esc(path)}"><td class="pathcell${isDir ? " dirrow" : ""}" ${indent} title="${esc(path)}">${label}</td>`;
  for (const layer of model.layers) {
    // A folder is "modified" whenever something inside it changes: true, but noise. Show only A and D.
    let c = row && !row.hidden ? row.cells[layer.index] : null;
    if (c && isDir && c.kind === "Modified") c = null;
    html += c
      ? `<td class="c ${c.kind}${isDir ? " dir" : ""}" data-path="${esc(path)}" data-layer="${layer.index}">${c.kind[0]}</td>`
      : "<td></td>";
  }
  // A folder only implied by the files inside it has no entry of its own: present if anything inside is.
  const sees = row?.sees ?? (line.type === "folder" && subtreeRows(line.node).some((r) => r.sees));
  const clickable = !isDir && row?.sees;
  return html + `<td class="sees${isDir ? " dir" : ""}${clickable ? " click" : ""}"${clickable ? ` data-path="${esc(path)}"` : ""}>${sees ? "&#10003;" : ""}</td></tr>`;
}

// ---------- detail panel ----------

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
  let html = `<h2 class="path">${esc(path)}</h2><div class="meta">Layer ${n}: ${esc(shortCmd(state.current.model.layers[layer].createdBy ?? ""))}</div>`;

  if (change.kind === "Deleted") {
    html += change.whiteoutMarker
      ? `<p class="what">Deleted in layer ${n}. The layer only holds a marker file, <code>${esc(change.whiteoutMarker)}</code>, that hides it.</p>`
      : `<p class="what">Hidden in layer ${n}. ${esc(change.note ?? "")}</p>`;
    html += earlier !== null
      ? `<p class="what">The file is <b>not</b> gone from the image: its data is still stored in layer ${earlier + 1}. ` +
        `<button type="button" class="link" data-show="${earlier}">Show it</button></p>`
      : "";
    body.innerHTML = html;
  } else if (row.entry === "Directory") {
    body.innerHTML = html + `<p class="what">Folder ${change.kind === "Added" ? "created" : "changed"} in layer ${n}.</p>`;
  } else if (row.entry === "Symlink") {
    body.innerHTML = html + `<p class="what">Symbolic link to <code>${esc(change.linkTarget ?? "?")}</code>.</p>`;
  } else if (fromResult) {
    html += `<p class="what">This is the version the container sees. It comes from layer ${n}.</p>` +
      `<div data-content="${layer}">Loading...</div>`;
    body.innerHTML = html;
    await loadContent(path, layer);
  } else {
    // A cell shows what that layer stores; the older version is one click away.
    html += change.kind === "Added"
      ? `<p class="what">Added in layer ${n}.</p>`
      : `<p class="what">Changed in layer ${n}. The layer stores the <b>whole</b> new file.</p>`;
    html += `<div data-content="${layer}">Loading...</div>`;
    if (change.kind === "Modified" && earlier !== null)
      html += `<p class="what">The old version is still stored in layer ${earlier + 1}. ` +
        `<button type="button" class="link" data-show="${earlier}">Show it</button></p>`;
    body.innerHTML = html;
    await loadContent(path, layer);
  }
  body.querySelectorAll("[data-show]").forEach((b) => b.addEventListener("click", () => showCell(path, Number(b.dataset.show))));
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
    if (c && c.kind === "Deleted") return null;
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
  if (!state.selected) return;
  document.querySelectorAll("#matrix tr[data-path]").forEach((tr) => {
    if (tr.dataset.path !== state.selected.path) return;
    tr.classList.add("sel");
    const cell = state.selected.layer === "result" ? "td.sees" : `td.c[data-layer="${state.selected.layer}"]`;
    tr.querySelector(cell)?.classList.add("selcell");
  });
}

// ---------- helpers ----------

async function getJson(url) {
  try {
    const r = await fetch(url);
    return r.ok ? await r.json() : null;
  } catch { return null; }
}

function showMessage(text, error = false) {
  const m = $("message");
  m.textContent = text;
  m.classList.toggle("error", error);
  m.hidden = false;
}
function hideMessage() { $("message").hidden = true; }

/** "RUN /bin/sh -c echo hi # buildkit" -> "RUN echo hi" */
function shortCmd(cmd) {
  return cmd.replace(/\/bin\/sh -c (#\(nop\)\s*)?/, "").replace(/\s*# buildkit$/, "").trim();
}

// First 12 hex digits, the way docker images and docker history show IDs.
function shortId(digest) {
  return (digest ?? "").replace(/^sha256:/, "").slice(0, 12);
}

function fmtSize(n) {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} kB`;
  return `${(n / 1024 / 1024).toFixed(1)} MB`;
}

function esc(s) {
  return String(s).replace(/[&<>"']/g, (ch) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[ch]);
}

const cmp = (a, b) => (a < b ? -1 : a > b ? 1 : 0);

async function copy(button) {
  try {
    await navigator.clipboard.writeText(button.previousElementSibling.textContent);
    button.textContent = "Copied";
    setTimeout(() => (button.textContent = "Copy"), 1200);
  } catch { /* clipboard blocked: the text is visible anyway */ }
}

function readSetting(key) { try { return localStorage.getItem("cv." + key); } catch { return null; } }
function writeSetting(key, value) { try { localStorage.setItem("cv." + key, value); } catch { /* private window */ } }

init();
