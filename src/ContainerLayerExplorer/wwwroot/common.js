// Helpers shared by the start page and the explorer page. Talks only to /api (see Api/Endpoints.cs). No framework.

export const UNREACHABLE = "Cannot reach Container Layer Explorer. Is it still running?";

export const $ = (id) => document.getElementById(id);

// ---------- talking to the server ----------

/**
 * GETs a JSON answer from the server. A failure throws an Error whose message is fit to show on the page:
 * the server's own { "error": "..." } text, or a sentence about the server itself being gone.
 */
export async function getJson(url) {
  let response;
  try {
    response = await fetch(url);
  } catch {
    throw new Error(UNREACHABLE);
  }
  let body = null;
  try {
    body = await response.json();
  } catch { /* the answer has no JSON body */ }
  if (!response.ok) throw new Error(body?.error ?? `The server answered ${response.status}.`);
  return body;
}

/**
 * Follows Docker's image events through the server (Server-Sent Events). The server keeps trying when Docker goes away
 * and comes back, and tells us with "disconnected" and "connected".
 * onEvent(action) gets every event; onLive(true or false) says whether Docker is being followed right now.
 */
export function listenToDocker(onEvent, onLive) {
  const source = new EventSource("/api/events");
  source.addEventListener("image", (e) => {
    const action = JSON.parse(e.data).action;
    onLive(action !== "disconnected");
    onEvent(action);
  });
  source.onerror = () => onLive(false); // EventSource reconnects by itself
  return source;
}

/** Runs fn once, ms after the last call. A docker build fires several events; the page reacts once. */
export function debounce(fn, ms) {
  let timer;
  return (...args) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), ms);
  };
}

// ---------- the message bar ----------

/** Shows a message in the bar under the header. With backLink, a link to the start page follows the text. */
export function showMessage(text, { error = false, backLink = false } = {}) {
  const bar = $("message");
  bar.textContent = text;
  if (backLink) {
    bar.append(" ");
    const link = document.createElement("a");
    link.href = "/";
    link.textContent = "Back to the image list";
    bar.append(link);
  }
  bar.classList.toggle("error", error);
  bar.hidden = false;
}

export function hideMessage() { $("message").hidden = true; }

// ---------- small helpers ----------

export function setLive(on) { $("live")?.classList.toggle("on", on); }

// First 12 hex digits, the way docker images and docker history show IDs.
export function shortId(digest) {
  return (digest ?? "").replace(/^sha256:/, "").slice(0, 12);
}

export function fmtSize(n) {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} kB`;
  return `${(n / 1024 / 1024).toFixed(1)} MB`;
}

export function esc(s) {
  return String(s).replace(/[&<>"']/g, (ch) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[ch]);
}

/** The address of the explorer page for one image. */
export function explorerUrl(imageName) {
  return `explorer.html?image=${encodeURIComponent(imageName)}`;
}

export function readSetting(key) { try { return localStorage.getItem("cle." + key); } catch { return null; } }
export function writeSetting(key, value) { try { localStorage.setItem("cle." + key, value); } catch { /* private window */ } }
