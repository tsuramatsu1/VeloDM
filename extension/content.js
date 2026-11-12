// Shows a small "Download this video" pill over a playing video or audio element, like IDM's
// download panel. Three cases:
//   - media served as a plain file (an http/https source): that file is sent to VeloDM;
//   - X and Facebook: page-hook.js finds the plain .mp4 version of the stream being played;
//   - any other chunked stream (blob: sources, e.g. YouTube, HLS/DASH players): the page address is
//     sent and VeloDM downloads the video with its video tools (yt-dlp).
// Copy-protected (DRM) video cannot be downloaded, so no pill is shown for it.

(() => {
  if (window.__velodmPill) return;
  window.__velodmPill = true;

  const MIN_WIDTH = 200;
  const MIN_HEIGHT = 110;
  const MIN_STREAM_SECONDS = 20; // short looping clips and animated previews are not worth a pill
  const dismissed = new WeakSet();
  let enabled = true;
  let current = null; // the media element the pill is attached to
  let pillHost = null;
  let label = null;
  let timer = 0;

  chrome.storage.local.get({ enabled: true, videoPill: true }, (s) => {
    enabled = s.enabled && s.videoPill;
  });
  chrome.storage.onChanged.addListener((changes) => {
    if (!changes.enabled && !changes.videoPill) return;
    chrome.storage.local.get({ enabled: true, videoPill: true }, (s) => {
      enabled = s.enabled && s.videoPill;
      if (!enabled) hide();
    });
  });

  // ---- streaming sites (X and Facebook) --------------------------------------------------
  // page-hook.js finds the plain .mp4 versions these sites list for each video and posts them
  // here. Each one comes with keys (X: the media id; Facebook: the stream's file paths) that
  // let us tell which video is the one playing.
  const host = location.hostname;
  const site = /(^|\.)(x|twitter)\.com$/.test(host) ? "x" : /(^|\.)facebook\.com$/.test(host) ? "fb" : null;
  const sniffed = new Map(); // key -> { url, quality }
  let lastKey = null; // key of the stream the player requested most recently

  function allowedMediaHost(url) {
    try {
      const h = new URL(url).hostname;
      return site === "x" ? h === "video.twimg.com" : h.endsWith(".fbcdn.net");
    } catch {
      return false;
    }
  }

  function retryPlaying() {
    if (current) return;
    for (const el of document.querySelectorAll("video")) {
      if (!el.paused && !el.ended) {
        show(el);
        if (current) return;
      }
    }
  }

  if (site) {
    window.addEventListener("message", (e) => {
      const d = e.data;
      if (e.source !== window || !d || d.velodm !== "media" || !Array.isArray(d.items)) return;
      for (const item of d.items.slice(-3000)) {
        if (!item || typeof item.url !== "string" || !Array.isArray(item.keys) || !allowedMediaHost(item.url)) continue;
        for (const key of item.keys.slice(0, 40)) {
          const known = sniffed.get(key);
          if (!known || (item.quality || 0) > known.quality) sniffed.set(key, { url: item.url, quality: item.quality || 0 });
        }
      }
      retryPlaying();
    });

    window.postMessage({ velodm: "replay" }, location.origin); // ask page-hook.js for what it already found

    // Watch which stream files the player requests, to know which video is playing.
    try {
      new PerformanceObserver((list) => {
        for (const entry of list.getEntries()) {
          let key = null;
          try {
            const u = new URL(entry.name);
            if (site === "x" && u.hostname === "video.twimg.com") key = (u.pathname.match(/\/(\d{8,})\//) || [])[1];
            else if (site === "fb" && u.hostname.endsWith(".fbcdn.net") && u.pathname.endsWith(".mp4")) key = u.pathname;
          } catch {}
          if (key) lastKey = key;
        }
        retryPlaying();
      }).observe({ type: "resource", buffered: true });
    } catch {}
  }

  function sniffedUrl(el) {
    if (!site) return null;
    if (site === "x") {
      // The poster image path carries the same media id as the video files.
      const id = ((el.poster || "").match(/\/(\d{8,})\//) || [])[1];
      if (id && sniffed.has(id)) return sniffed.get(id).url;
    }
    if (lastKey) return sniffed.has(lastKey) ? sniffed.get(lastKey).url : null;
    // Nothing has been requested yet, so a single known video can only be the one on screen.
    const urls = new Set([...sniffed.values()].map((v) => v.url));
    return site === "fb" && urls.size === 1 ? [...urls][0] : null;
  }

  function mediaUrl(el) {
    const candidates = [el.currentSrc, el.src, ...[...el.querySelectorAll("source")].map((s) => s.src)];
    return candidates.find((u) => /^https?:/i.test(u || "")) || sniffedUrl(el);
  }

  // Whether this page's address identifies one video, so VeloDM's video tools can be pointed at it.
  // On Facebook and X that is a reel, a watch page or a single post; their feeds are not.
  function pageNamesTheVideo() {
    if (site === "fb") return /^\/(reel|reels|watch|share\/[vr]|[^/]+\/videos)(\/|$)/.test(location.pathname);
    if (site === "x") return /\/status\/\d+/.test(location.pathname);
    return true;
  }

  // A chunked stream: the player feeds the element from script, so there is no file address to send.
  function isStream(el) {
    if (el.tagName !== "VIDEO" || el.mediaKeys || !pageNamesTheVideo()) return false;
    const src = el.currentSrc || el.src || "";
    if (src && !/^(blob|mediasource):/i.test(src)) return false;
    if (!src && !el.srcObject) return false;
    // Reels and posts are often only a few seconds long; elsewhere short clips are previews and decoration.
    return site ? true : !(el.duration < MIN_STREAM_SECONDS); // unknown and live durations pass
  }

  function downloadable(el) {
    return !!mediaUrl(el) || isStream(el);
  }

  function fileNameFor(el, url) {
    let ext = "";
    try {
      ext = (new URL(url).pathname.match(/\.[a-z0-9]{2,5}$/i) || [""])[0];
    } catch {}
    if (!ext) ext = el.tagName === "AUDIO" ? ".mp3" : ".mp4";
    const title = (document.title || "video").replace(/[\\/:*?"<>|]+/g, " ").trim().slice(0, 120) || "video";
    return title + ext;
  }

  function build() {
    pillHost = document.createElement("div");
    pillHost.style.cssText = "position:fixed;z-index:2147483647;top:0;left:0;display:none;";
    const root = pillHost.attachShadow({ mode: "closed" });
    root.innerHTML = `
      <style>
        .pill { display:flex; align-items:center; font:600 12px/1 "Segoe UI",system-ui,sans-serif; color:#fff;
                background:linear-gradient(135deg,#1ec8ff,#3a3fe0); border-radius:999px;
                box-shadow:0 2px 10px rgba(0,0,0,.35); overflow:hidden; white-space:nowrap; user-select:none; }
        button { all:unset; cursor:pointer; padding:8px 12px; color:#fff; }
        button:hover { background:rgba(255,255,255,.18); }
        .close { padding:8px 10px 8px 6px; opacity:.85; }
      </style>
      <div class="pill">
        <button class="go" title="Download this video with VeloDM">&#x2B07;&nbsp; <span>Download this video</span></button>
        <button class="close" title="Hide">&#x2715;</button>
      </div>`;
    label = root.querySelector("span");
    root.querySelector(".go").addEventListener("click", download);
    root.querySelector(".close").addEventListener("click", () => {
      if (current) dismissed.add(current);
      hide();
    });
    (document.body || document.documentElement).appendChild(pillHost);
  }

  function show(el) {
    if (!enabled || dismissed.has(el) || !downloadable(el)) return;
    if (!pillHost) build();
    current = el;
    label.textContent = el.tagName === "AUDIO" ? "Download this audio" : "Download this video";
    place();
    clearInterval(timer);
    timer = setInterval(place, 300);
  }

  function hide() {
    clearInterval(timer);
    current = null;
    if (pillHost) pillHost.style.display = "none";
  }

  function place() {
    if (!current || !current.isConnected || !downloadable(current)) return hide();
    const r = current.getBoundingClientRect();
    const isAudio = current.tagName === "AUDIO";
    const visible = r.bottom > 0 && r.top < innerHeight && r.right > 0 && r.left < innerWidth &&
      (isAudio ? r.width > 0 : r.width >= MIN_WIDTH && r.height >= MIN_HEIGHT);
    if (!visible) {
      pillHost.style.display = "none";
      return;
    }
    pillHost.style.display = "block";
    const w = pillHost.offsetWidth;
    pillHost.style.left = Math.max(4, Math.min(r.right - w - 8, innerWidth - w - 4)) + "px";
    pillHost.style.top = Math.max(4, r.top + 8) + "px";
  }

  function download() {
    const el = current;
    if (!el) return;
    const url = mediaUrl(el);
    if (!url && !isStream(el)) return;
    const original = label.textContent;
    // Facebook and X page titles describe the site, not the video.
    const title = site ? "" : (document.title || "").trim().slice(0, 150);
    chrome.runtime.sendMessage(
      url
        ? { type: "download", url, referrer: location.href, fileName: fileNameFor(el, url) }
        : { type: "stream", title },
      (reply) => {
        const ok = !chrome.runtime.lastError && reply && reply.ok;
        label.textContent = ok ? "Sent to VeloDM" : "VeloDM is not running";
        setTimeout(() => {
          if (label) label.textContent = original;
        }, 2500);
      },
    );
  }

  // Clicking a magnet link hands it straight to VeloDM. If the app is not running, the link is
  // followed normally so whatever Windows has registered for magnet links still gets it.
  document.addEventListener("click", (e) => {
    if (!enabled || e.button !== 0 || e.defaultPrevented) return;
    const link = e.target instanceof Element ? e.target.closest("a[href]") : null;
    if (!link || !/^magnet:/i.test(link.href)) return;
    e.preventDefault();
    e.stopPropagation();
    chrome.runtime.sendMessage({ type: "download", url: link.href, referrer: location.href }, (reply) => {
      if (chrome.runtime.lastError || !reply || !reply.ok) location.href = link.href;
    });
  }, true);

  // Media events do not bubble, so listen in the capture phase.
  for (const type of ["play", "playing", "loadedmetadata"]) {
    document.addEventListener(type, (e) => {
      const el = e.target;
      if (el instanceof HTMLMediaElement && (type !== "loadedmetadata" || !current)) show(el);
    }, true);
  }

  // Media that was already playing before this script loaded.
  for (const el of document.querySelectorAll("video, audio")) {
    if (!el.paused) {
      show(el);
      break;
    }
  }
})();
