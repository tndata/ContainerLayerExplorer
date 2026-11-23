// Container Layer Explorer: the Stack view of the explorer page. It draws the image the way it is usually taught:
// the layers as a stack, the first build step at the bottom and the last on top, with a header of image facts.
// Master and detail: the image box (around the whole stack), a layer or a step without a layer can be selected, and
// the right side shows "What this image reveals", the layer's details or the step's details.
// Everything comes from the model /api/image already returned. No framework.
import { $, esc, fmtSize, shortCmd, shortId } from "./common.js";

// What is selected: "image", "layer:<layer index>" or "step:<history index>". Kept across rebuilds of the same image.
let selected = "image";
let selectedFor = null;   // the image ID the selection belongs to
let shown = null;         // { image, previous } of the last render, so a selection can redraw the detail

/**
 * Draws the whole Stack view.
 * image: { id, name, model } from /api/image. tags: every tag of the image. previous: the diff IDs of the build shown
 * before, or undefined (for the NEW badges).
 */
export function renderStackView(image, { tags, previous }) {
  const { model } = image;
  if (selectedFor !== image.id || !exists(model, selected)) selected = "image";
  selectedFor = image.id;
  shown = { image, previous };
  $("stack-head").innerHTML = headerHtml(image, tags);
  const box = $("image-box");
  box.innerHTML = imageBoxHtml(image, previous ? new Set(previous) : null);
  box.onclick = (e) => select(e.target.closest("[data-select]")?.dataset.select ?? "image");
  box.onkeydown = (e) => {
    if (e.key !== "Enter" && e.key !== " ") return;
    const target = e.target.closest("[data-select]");
    if (!target) return;
    e.preventDefault();
    select(target.dataset.select);
  };
  renderDetail();
}

/** Escape in the Stack view: back to the image. */
export function selectStackImage() {
  if (shown) select("image");
}

function select(what) {
  selected = what;
  renderDetail();
}

function exists(model, what) {
  const [kind, n] = what.split(":");
  if (kind === "layer") return !!model.layers[Number(n)];
  if (kind === "step") return !!model.history[Number(n)];
  return true;
}

/** Marks the selected item and fills the right side. */
function renderDetail() {
  const { image, previous } = shown;
  document.querySelectorAll("#image-box [data-select]").forEach((el) => {
    const on = el.dataset.select === selected;
    el.classList.toggle("selected", on);
    el.setAttribute("aria-pressed", String(on));
  });
  $("image-box").classList.toggle("selected", selected === "image");
  const [kind, n] = selected.split(":");
  const detail = $("stack-detail");
  if (kind === "layer") detail.innerHTML = layerDetailHtml(image.model, Number(n), previous ? new Set(previous) : null);
  else if (kind === "step") detail.innerHTML = stepDetailHtml(image.model, Number(n));
  else detail.innerHTML = revealsHtml(image.model);
  detail.querySelectorAll("button.why").forEach((b) => b.addEventListener("click", () => {
    const hint = $(b.getAttribute("aria-controls"));
    hint.hidden = !hint.hidden;
    b.setAttribute("aria-expanded", String(!hint.hidden));
  }));
}

// ---------- the header ----------

function headerHtml(image, tags) {
  const { model } = image;
  const stored = model.layers.reduce((sum, l) => sum + l.storedSize, 0);
  const emptySteps = model.history.filter((s) => s.layerIndex === null).length;
  const platform = [model.os, model.architecture].filter(Boolean).join("/") || "not recorded";
  const tagList = (tags.length ? tags : [image.name]).map((t) => `<code class="tag">${esc(t)}</code>`).join(" ");
  const fact = (label, value, tip = "") => {
    const titleAttr = tip ? ` title="${esc(tip)}"` : "";
    return `<div class="fact"${titleAttr}><dt>${label}</dt><dd>${value}</dd></div>`;
  };

  return `<h2>${esc(image.name)}</h2>` +
    `<div class="tags">Tags: ${tagList}</div>` +
    (model.truncated ? `<p class="warning">Too many files: only the first part of the image was read, so the sizes are too small.</p>` : "") +
    `<dl class="facts">` +
    fact("Image ID", `<code class="imageid-code" title="${esc(image.id)}">${shortId(image.id)}</code>`) +
    fact("Created", model.created ? esc(fmtTime(model.created)) : `<span class="none">not recorded</span>`,
      "The image config's created time, as stored, with its time zone.") +
    fact("Platform", esc(platform)) +
    fact("Layers", `${model.layers.length}` +
      ` <span class="sub">(${model.history.length} build steps, ${emptySteps} without a layer)</span>`) +
    fact("Size", `${fmtSize(model.totalSize)} of files <span class="sub">(${fmtSize(stored)} stored, ${compressionText(model.layers)})</span>`,
      "The files in all layers added up, and the size of the layer files in Docker's export of the image. Docker's own size figure counts differently.") +
    `</dl>`;
}

// ---------- the stack ----------

/** The box around the whole stack: the image name on top, then one entry per build step, the last step first,
 *  so the first step ends up at the bottom like on a slide. The box itself selects the image. */
function imageBoxHtml(image, prevSet) {
  return `<div class="image-box-title" data-select="image" tabindex="0" role="button" title="Show what this image reveals">` +
    `<span class="imagename">${esc(image.name)}</span> <span class="sub">the image: click to see what it reveals</span></div>` +
    `<div class="stack">${stackHtml(image.model, prevSet)}</div>` +
    `<p class="note">Top: the last build step. Bottom: the first. Click a layer or a step for its details.</p>`;
}

function stackHtml(model, prevSet) {
  const items = model.history.map((step, i) => ({ step, at: i, layer: step.layerIndex === null ? null : model.layers[step.layerIndex] }));
  // A layer that no history step names (a short history) still belongs in the stack, after the others.
  const named = new Set(model.history.map((s) => s.layerIndex).filter((i) => i !== null));
  for (const layer of model.layers) if (!named.has(layer.index)) items.push({ step: null, at: null, layer });
  if (items.length === 0) return `<p class="note">This image has no layers and no recorded build steps.</p>`;
  return items.reverse().map(({ step, at, layer }) => layer ? layerHtml(layer, step, prevSet) : emptyStepHtml(step, at)).join("");
}

function layerHtml(layer, step, prevSet) {
  const isNew = prevSet && !prevSet.has(layer.diffId);
  const cmd = layer.createdBy ?? step?.createdBy ?? "";
  const tip = [cmd, `Layer digest: ${layer.diffId}`, step?.created ? `Build time: ${fmtTime(step.created)}` : "",
    "Click for the layer's details."].filter(Boolean).join("\n");
  return `<div class="stack-layer" data-select="layer:${layer.index}" tabindex="0" role="button" aria-pressed="false" title="${esc(tip)}">` +
    `<span class="n">${layer.index + 1}</span>` +
    `<code class="cmd">${esc(shortCmd(cmd)) || "<i>(not recorded)</i>"}</code>` +
    (isNew ? `<span class="badge new">new</span>` : "") +
    `<span class="lid"><code>${shortId(layer.diffId)}</code></span>` +
    `<span class="sizes">${fmtSize(layer.storedSize)} stored<br>${fmtSize(layer.size)} of files</span>` +
    `</div>`;
}

function emptyStepHtml(step, at) {
  const tip = [step.createdBy, step.created ? `Build time: ${fmtTime(step.created)}` : "",
    "This step makes no layer: it only changes the image settings. Click for its details."].filter(Boolean).join("\n");
  return `<div class="stack-step" data-select="step:${at}" tabindex="0" role="button" aria-pressed="false" title="${esc(tip)}">` +
    `<span class="zero">0 B</span><code class="cmd">${esc(shortCmd(step.createdBy ?? ""))}</code></div>`;
}

// ---------- What this image reveals ----------

/**
 * One row per fact from the image config or the manifest. A row with no value is shown greyed with what is missing.
 * The wording stays neutral: it says what is there, not whether it is good or bad.
 */
function revealsHtml(model) {
  const rows = [
    ...buildRevealRows(model),
    ...startRevealRows(model.config),
    ...runtimeRevealRows(model.config),
    ...layerRevealRows(model),
  ];
  return `<h2>What this image reveals</h2>` +
    `<p class="note">Read from the image config and manifest. Anyone who has a copy of the image can read the same. Hover over a row, or click ?, to see why it matters.</p>` +
    `<div class="reveal-rows">${rows.join("")}</div>`;
}

/** How the image was built and by whom: created, build steps, build times, author and labels. */
function buildRevealRows(model) {
  const steps = model.history;
  const dates = steps.map((s) => s.created).filter(Boolean).sort();
  const labels = Object.entries(model.config.labels ?? {});
  return [
    row("Created", "config", "created", model.created && esc(fmtTime(model.created)), "no creation time",
      "When the image was built, as the builder recorded it. Shown as stored, with its time zone offset."),
    row("Build steps", "config", "history", steps.length && buildStepsHtml(steps), "no build history",
      "The Dockerfile instructions as they were run, with the full text of every RUN command. Anything typed on a RUN line, such as a password, can be read here. The Dockerfile itself is not in the image config."),
    row("Build times", "config", "history[].created", dates.length && buildTimesHtml(dates),
      "no build times",
      "Each build step has its own time. Steps that come from a base image are often older than the steps of the image's own Dockerfile."),
    row("Author", "config", "author", model.author && esc(model.author), "no author",
      "A name set with MAINTAINER or docker commit --author. Many images leave it empty and use a label instead."),
    row("Labels", "config", "config.Labels", labels.length &&
      list(labels.map(([k, v]) => `${esc(k)} = ${esc(v)}`)), "no labels",
      "Whatever the image author chose to publish. The OCI keys org.opencontainers.image.* can name the source repository, the git commit, the version and the licence."),
  ];
}

function buildStepsHtml(steps) {
  const buildkit = steps.filter((s) => (s.createdBy ?? "").trimEnd().endsWith("# buildkit")).length;
  const withLayer = steps.filter((s) => s.layerIndex !== null).length;
  const buildkitNote = buildkit ? `, ${buildkit} marked <code># buildkit</code>` : "";
  return `${steps.length} steps, ${withLayer} with a layer` + buildkitNote;
}

/** One time, or the first to the last when the steps differ. dates is sorted and not empty. */
function buildTimesHtml(dates) {
  const first = dates[0];
  const last = dates[dates.length - 1];
  return first === last ? esc(fmtTime(first)) : `${esc(fmtTime(first))} to ${esc(fmtTime(last))}`;
}

/** What a container starts with: environment, entrypoint, command, working directory and user. */
function startRevealRows(c) {
  return [
    row("Environment", "config", "config.Env", c.env.length && list(c.env.map(esc)), "no environment variables",
      "Every ENV value is stored in the image config and travels with every copy of the image, including a value that was meant to be secret."),
    row("Entrypoint", "config", "config.Entrypoint", c.entrypoint.length && `<code>${esc(c.entrypoint.join(" "))}</code>`, "no entrypoint",
      "The program that runs when a container starts."),
    row("Command", "config", "config.Cmd", c.cmd.length && `<code>${esc(c.cmd.join(" "))}</code>`, "no command",
      "The default command, or the default arguments to the entrypoint."),
    row("Working directory", "config", "config.WorkingDir", c.workingDir && `<code>${esc(c.workingDir)}</code>`, "not set (/)",
      "The folder the container starts in, set with WORKDIR. Often where the application's files are."),
    row("User", "config", "config.User", c.user && `<code>${esc(c.user)}</code>`, "not set (runs as root by default)",
      "The user the container runs as, set with USER. When it is not set, the processes run as root unless another user is given when the container starts."),
  ];
}

/** How the container runs: exposed ports, volumes, health check, stop signal and shell. */
function runtimeRevealRows(c) {
  return [
    row("Exposed ports", "config", "config.ExposedPorts", c.exposedPorts.length && `<code>${esc(c.exposedPorts.join(", "))}</code>`, "no exposed ports",
      "The ports the author says the application listens on. EXPOSE documents a port; it does not open it."),
    row("Volumes", "config", "config.Volumes", c.volumes.length && `<code>${esc(c.volumes.join(", "))}</code>`, "no volumes",
      "Folders that get a volume when a container starts: where the image expects its data to live."),
    row("Health check", "config", "config.Healthcheck", c.healthcheck && healthHtml(c.healthcheck), "no health check",
      "How Docker tests that the application inside is healthy, set with HEALTHCHECK."),
    row("Stop signal", "config", "config.StopSignal", c.stopSignal && `<code>${esc(c.stopSignal)}</code>`, "not set (SIGTERM)",
      "The signal Docker sends to stop the container, set with STOPSIGNAL."),
    row("Shell", "config", "config.Shell", c.shell.length && `<code>${esc(c.shell.join(" "))}</code>`, "not set (the default shell)",
      "The shell used for the shell form of RUN, CMD and ENTRYPOINT, set with SHELL."),
  ];
}

/** The platform and the layers: platform, layer fingerprints and stored layers. */
function layerRevealRows(model) {
  const stored = model.layers.reduce((sum, l) => sum + l.storedSize, 0);
  return [
    row("Platform", "config", "os, architecture", [model.os, model.architecture].filter(Boolean).map(esc).join("/"), "not recorded",
      "The platform the image is built for. It names the target, not the computer that built the image."),
    row("Layer fingerprints", "config", "rootfs.diff_ids", model.layers.length && `${model.layers.length} content hashes`, "no layers",
      "A hash of each layer's content. The same layer has the same fingerprint in every image that contains it."),
    row("Stored layers", "export", "the layer files", model.layers.length &&
      `${model.layers.length} layer blobs, ${fmtSize(stored)}, ${compressionText(model.layers)}`, "no layers",
      "The layer files as Docker exported them. When they are gzip compressed, this is roughly what a push or a pull transfers. Some Docker setups export the layers uncompressed, and then the size is close to the file data."),
  ];
}

// ---------- a layer's details ----------

/** The details of one layer: from the history (config), the manifest, and the layer's own tar entries. */
function layerDetailHtml(model, index, prevSet) {
  const layer = model.layers[index];
  const at = model.history.findIndex((s) => s.layerIndex === index);
  const step = at >= 0 ? model.history[at] : null;
  const f = layer.facts;
  const isNew = prevSet && !prevSet.has(layer.diffId);
  const newBadge = isNew ? ' <span class="badge new">new</span>' : "";

  const rows = [
    row("Build step", "config", "history[].created_by", (layer.createdBy ?? step?.createdBy) && `<pre>${esc(layer.createdBy ?? step.createdBy)}</pre>`,
      "not recorded", "The Dockerfile instruction as it was run. A RUN line is stored word for word, including any value typed on it."),
    row("Build time", "config", "history[].created", step?.created && esc(fmtTime(step.created)), "not recorded",
      "When this step ran, as the builder recorded it. A cached step keeps the time of the build that first made it."),
    row("Content fingerprint", "config", "rootfs.diff_ids", layer.diffId && `<code>${esc(layer.diffId)}</code>`, "not recorded",
      "The hash of the layer's uncompressed content. The same layer has the same fingerprint in every image that contains it."),
    row("Blob digest", "manifest", "layers[].digest", layer.blobDigest && `<code>${esc(layer.blobDigest)}</code>`, "not recorded in this archive format",
      "The hash of the layer as stored and sent (usually gzip compressed). Registries and docker pull use this one."),
    row("Stored size", "export", "the layer file's size", storedSizeHtml(layer), "",
      "The size of this layer's file in Docker's export of the image. When it is gzip compressed, this is roughly what a push or a pull transfers. The percentage compares it with the files' total size; tar headers make a tiny layer bigger than its files."),
    row("Size of files", "layer", "tar entries", `${fmtSize(layer.size)}`, "",
      "The files in this layer added up, uncompressed. Deleted files are only markers here: their data is in a lower layer."),
  ];
  if (f) rows.push(...layerFactRows(f));
  return `<div class="detail-head"><h2>Layer ${index + 1} <span class="sub">of ${model.layers.length}</span>${newBadge}</h2>` +
    `</div>` +
    `<p class="note">Facts from the image config, the manifest and the layer's file list. Hover over a row, or click ?, to see why it matters.</p>` +
    (model.truncated ? `<p class="warning">Too many files: only the first part of the image was read, so the counts may be too small.</p>` : "") +
    `<div class="reveal-rows">${rows.join("")}</div>`;
}

/** The stored size and whether it is compressed, with how much of the file data it is when the layer has files. */
function storedSizeHtml(layer) {
  const stored = `${fmtSize(layer.storedSize)}, ${layer.storedGzip ? "gzip compressed" : "not compressed"}`;
  if (layer.size > 0) {
    const ratio = `${Math.round((layer.storedSize * 100) / layer.size)}% of the file data`;
    return `${stored} <span class="sub">(${ratio})</span>`;
  }
  return stored;
}

/** "gzip compressed", "not compressed" or "partly compressed", from what the export holds. */
function compressionText(layers) {
  const gzip = layers.filter((l) => l.storedGzip).length;
  if (gzip === layers.length) return "gzip compressed";
  return gzip === 0 ? "not compressed" : "partly compressed";
}

function layerFactRows(f) {
  const changes = [
    f.added && `<span class="a">+${plural(f.added, "file")} added</span> (${fmtSize(f.addedSize)})`,
    f.modified && `<span class="m">~${plural(f.modified, "file")} changed</span> (${fmtSize(f.modifiedSize)})`,
    f.deleted && `<span class="d">&minus;${plural(f.deleted, "file")} deleted</span> (${fmtSize(f.deletedSize)})`,
  ].filter(Boolean);
  const entries = [
    plural(f.files, "file"), plural(f.folders, "folder"), plural(f.symlinks, "symlink"), f.otherEntries && plural(f.otherEntries, "other entry", "other entries"),
  ].filter(Boolean);
  return [
    row("Changes", "layer", "tar entries", changes.length && changes.join("<br>"), "no changes",
      "Added and changed count files only; deleted counts every path that disappears, the contents of a deleted folder included. The size of a deleted file is the data still stored in the layer below it. A changed file is stored whole again. A deleted file is only hidden: a whiteout marker in this layer hides it, and its data stays in the layer below."),
    row("Entries", "layer", "tar entries", entries.length && entries.join(", "), "no entries",
      "What the layer's tar file holds besides the deletions. Folders show up whenever something inside them changes."),
    row("Biggest files", "layer", "tar entries", f.biggest.length && biggestHtml(f.biggest),
      "no files with content", "Where the size of this layer comes from."),
    row("Hidden by later layers", "layer", "tar entries of all layers", f.hiddenLaterFiles &&
      `${fmtSize(f.hiddenLaterSize)} in ${plural(f.hiddenLaterFiles, "file")}`, "none: a container sees every file of this layer",
      "Files of this layer that a later layer changes or deletes. A container no longer sees them, but they are still stored in the image and anyone with the image can extract them."),
    row("Executable files", "layer", "tar entry mode", f.executables && plural(f.executables, "file"), "none",
      "Files with an execute bit in their permissions."),
    row("Setuid or setgid", "layer", "tar entry mode", f.setIdCount && modeList(f.setIdFiles, f.setIdCount), "none",
      "A setuid or setgid program runs with the rights of its owner or group, not of the user who starts it. Base images often have a few, such as su and passwd."),
    row("World-writable", "layer", "tar entry mode", f.worldWritableCount && modeList(f.worldWritable, f.worldWritableCount), "none",
      "Files and folders that every user in the container may change. /tmp is usually one, with the sticky bit (1777) so users cannot delete each other's files."),
  ];
}

// ---------- a step without a layer ----------

function stepDetailHtml(model, at) {
  const step = model.history[at];
  const rows = [
    row("Build step", "config", "history[].created_by", step.createdBy && `<pre>${esc(step.createdBy)}</pre>`, "not recorded",
      "The Dockerfile instruction as it was run, word for word as the builder recorded it."),
    row("Build time", "config", "history[].created", step.created && esc(fmtTime(step.created)), "not recorded",
      "When this step ran, as the builder recorded it."),
    row("Layer", "config", "history[].empty_layer", "none: this step changes only the image config (settings such as ENV, LABEL or CMD) and adds no files", "",
      "Only steps that change files make a layer. The others are recorded in the history and change the config, so they change the image ID but not its size."),
  ];
  // Docker records a HEALTHCHECK step as a Go struct dump. That text stays as it is; the config holds the same setting in a readable form.
  if ((step.createdBy ?? "").includes("HEALTHCHECK") && model.config.healthcheck) {
    rows.push(row("Health check in the config", "config", "config.Healthcheck", healthHtml(model.config.healthcheck), "",
      "The health check the image has now, read from the config. A later HEALTHCHECK step would replace this one."));
  }
  return `<div class="detail-head"><h2>Step without a layer</h2></div>` +
    `<p class="note">Hover over a row, or click ?, to see why it matters.</p>` +
    `<div class="reveal-rows">${rows.join("")}</div>`;
}

function plural(n, one, many = one + "s") {
  return `${n} ${n === 1 ? one : many}`;
}

function biggestHtml(files) {
  const items = files.map((b) => `<li><code>${esc(b.path)}</code> <span class="sub">${fmtSize(b.size)}</span></li>`).join("");
  return `<ul class="values">${items}</ul>`;
}

function modeList(items, count) {
  const more = count > items.length ? `<li class="sub">and ${count - items.length} more</li>` : "";
  const entries = items.map((m) => `<li><code>${esc(m.path)}</code> <span class="sub">${octal(m.mode)}</span></li>`).join("");
  return `<ul class="values">${entries}${more}</ul>`;
}

/** 0o4755 -> "4755", 0o644 -> "0644", as ls and chmod write it. */
function octal(mode) {
  return (mode & 0o7777).toString(8).padStart(4, "0");
}

// ---------- rows ----------

const SOURCES = {
  config: "the image config",
  manifest: "the image manifest",
  export: "Docker's export of the image (the same as docker save)",
  layer: "the layer's file list (tar headers, not file contents)",
};

let rowCount = 0;

/** value is HTML, or something falsy when the image has nothing here. */
function row(label, source, field, value, emptyText, why) {
  const id = `why-${++rowCount}`;
  const has = !!value;
  const sourceTip = esc(`Read from ${SOURCES[source]}: ${field}`);
  return `<div class="reveal${has ? "" : " empty"}" title="${esc(why)}">` +
    `<div class="reveal-label">${label}</div>` +
    `<div class="reveal-value">${has ? value : esc(emptyText)}</div>` +
    `<span class="source ${source}" title="${sourceTip}">${source}</span>` +
    `<button type="button" class="why" aria-expanded="false" aria-controls="${id}" aria-label="Why it matters: ${esc(label)}">?</button>` +
    `<p class="why-text" id="${id}" hidden>${esc(why)}</p></div>`;
}

function list(items) {
  const entries = items.map((i) => `<li><code>${i}</code></li>`).join("");
  return `<ul class="values">${entries}</ul>`;
}

function healthHtml(h) {
  if (h.test.length === 1 && h.test[0] === "NONE") return "turned off (HEALTHCHECK NONE)";
  const kind = h.test[0];
  const command = h.test.slice(1).join(" ");
  const timing = [
    h.interval && `every ${fmtDuration(h.interval)}`,
    h.timeout && `timeout ${fmtDuration(h.timeout)}`,
    h.startPeriod && `start period ${fmtDuration(h.startPeriod)}`,
    h.retries && `${h.retries} retries`,
  ].filter(Boolean).join(", ");
  const timingHtml = timing ? ` <span class="sub">(${esc(timing)})</span>` : "";
  return `<code>${esc(kind)} ${esc(command)}</code>${timingHtml}`;
}

// ---------- formatting ----------

/** "2026-11-05T07:17:53.7143132+00:00" -> "2026-11-05 07:17:53 UTC", as stored, not turned into local time. */
function fmtTime(s) {
  const m = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2}:\d{2})(?:\.\d+)?(Z|[+-]\d{2}:\d{2})?$/.exec(s);
  if (!m) return s;
  const zone = !m[3] || m[3] === "Z" || m[3] === "+00:00" ? "UTC" : `UTC${m[3]}`;
  return `${m[1]} ${m[2]} ${zone}`;
}

/** A .NET TimeSpan in JSON ("00:00:30", "1.02:00:00") -> "30 s", "1 min 30 s". */
function fmtDuration(s) {
  const m = /^(?:(\d+)\.)?(\d+):(\d+):(\d+)/.exec(s);
  if (!m) return s;
  const total = ((Number(m[1] ?? 0) * 24 + Number(m[2])) * 60 + Number(m[3])) * 60 + Number(m[4]);
  const min = Math.floor(total / 60), sec = total % 60;
  if (min === 0) return `${sec} s`;
  return sec === 0 ? `${min} min` : `${min} min ${sec} s`;
}
