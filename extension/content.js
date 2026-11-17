// Shows a small "Download this video" pill over a playing video or audio element, like IDM's
// download panel. Three cases:
//   - media served as a plain file (an http/https source): that file is sent to VeloDM;
//   - X and Facebook: page-hook.js finds the plain .mp4 version of the stream being played;
//   - any other chunked stream (blob: sources, e.g. YouTube, HLS/DASH players): the page address is
//     sent and VeloDM downloads the video with its video tools (yt-dlp);
//   - Facebook stories and feed videos, which have neither a plain file nor a page of their own: the
//     picture file and the sound file the player fetches are sent, and VeloDM joins them.
// Copy-protected (DRM) video cannot be downloaded, so no pill is shown for it.

(() => {
  // A copy of this script left by an earlier load of the extension goes dead when the extension
  // is updated or reloaded: it can no longer reach the background script. Only a live copy keeps
  // a new one out, and whatever pill a dead one left on the page is taken away.
  const alive = () => {
    try {
      return !!chrome.runtime.id;
    } catch {
      return false;
    }
  };
  if (typeof window.__velodmPillAlive === "function" && window.__velodmPillAlive()) return;
  window.__velodmPillAlive = alive;
  for (const el of document.querySelectorAll("div[data-velodm-pill], body > div[style], html > div[style]")) {
    // Copies before 1.5.5 did not mark their pill: an empty, fixed, top-most div with nothing but a style.
    const unmarked = !el.firstChild && el.attributes.length === 1 && el.style.position === "fixed" && el.style.zIndex === "2147483647";
    if (el.hasAttribute("data-velodm-pill") || unmarked) el.remove();
  }

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
            else if (site === "fb" && u.hostname.endsWith(".fbcdn.net")) {
              fbRequests++;
              if (isVideoFile(u)) {
                key = u.pathname;
                notePlayerFile(u);
              }
            }
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

  // ---- Facebook: the files the player itself fetches ---------------------------------------
  // Where no plain .mp4 is listed for a video and its page has no address of its own (stories,
  // videos in the feed), the player still has to fetch the stream: one file for the picture and
  // one for the sound, a range at a time. Each address describes itself in its "efg" value (kind
  // and quality, which video it belongs to, how long that is), and without the range it returns
  // the whole file. VeloDM downloads the two and joins them.
  const playerFiles = new Map(); // file path -> description, least recently fetched first
  let playerFileCount = 0;
  let fbRequests = 0; // everything fetched from Facebook's file servers, for the note in VeloDM's log

  function describedBy(u) {
    try {
      return JSON.parse(atob((u.searchParams.get("efg") || "").replace(/-/g, "+").replace(/_/g, "/"))) || {};
    } catch {
      return {};
    }
  }

  // Video and sound files are named .mp4, are fetched a range at a time, or say what encoding they are.
  function isVideoFile(u) {
    if (/\.(jpe?g|png|webp|gif|heic|js|css)$/i.test(u.pathname)) return false;
    return u.pathname.endsWith(".mp4") || u.searchParams.has("bytestart") || !!describedBy(u).vencode_tag;
  }

  function notePlayerFile(u) {
    const info = describedBy(u);
    const tag = String(info.vencode_tag || "");
    // Text surgery rather than URL parsing, so the signed address is otherwise left exactly as it was.
    const [base, query = ""] = u.href.split("#")[0].split("?");
    const kept = query.split("&").filter((part) => part && !/^(bytestart|byteend)=/.test(part));
    playerFiles.delete(u.pathname); // re-added below so the map stays ordered by last use
    playerFiles.set(u.pathname, {
      url: base + (kept.length ? "?" + kept.join("&") : ""),
      audio: /audio/i.test(tag),
      height: Number((tag.match(/(\d{3,4})p/) || [])[1]) || 0,
      asset: String(info.xpv_asset_id || info.video_id || ""),
      duration: Number(info.duration_s) || 0,
      tag,
      order: ++playerFileCount,
    });
    if (playerFiles.size > 300) playerFiles.delete(playerFiles.keys().next().value);
  }

  // The picture and sound files of the video this element is playing, or null if they cannot be told.
  function playerFilesFor(el) {
    if (site !== "fb" || el.tagName !== "VIDEO" || el.mediaKeys || !playerFiles.size) return null;
    let files = [...playerFiles.values()];
    // The player fetches the next stories ahead of time; the length tells this video's files from theirs.
    if (el.duration > 0 && isFinite(el.duration)) {
      const sameLength = files.filter((f) => f.duration && Math.abs(f.duration - el.duration) <= 1.5);
      if (sameLength.length) files = sameLength;
    }
    const newest = files[files.length - 1];
    // Files that do not say which video they belong to: only the last few fetched can be this one's.
    const ofVideo = newest.asset ? files.filter((f) => f.asset === newest.asset) : files.slice(-4);
    const pictures = ofVideo.filter((f) => !f.audio).sort((a, b) => b.height - a.height || b.order - a.order);
    if (!pictures.length) return null;
    // The sound of the same video, or failing that the sound fetched most recently.
    const sound = [...ofVideo, ...files].filter((f) => f.audio).sort((a, b) => (b.asset === newest.asset) - (a.asset === newest.asset) || b.order - a.order)[0];
    return {
      url: pictures[0].url,
      audioUrl: sound ? sound.url : "",
      note: `player files: picture "${pictures[0].tag}", sound "${sound ? sound.tag : "none"}", video ${newest.asset || "?"}, ` +
        `${el.duration > 0 ? Math.round(el.duration) + " s, " : ""}${ofVideo.length} of ${playerFiles.size} files matched`,
    };
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
    return !!mediaUrl(el) || isStream(el) || !!playerFilesFor(el);
  }

  // What to send to VeloDM for this element, or null when nothing about it can be downloaded.
  // "from" (pill or menu) goes into the note VeloDM writes to its log on Facebook and X.
  function requestFor(el, from) {
    const url = mediaUrl(el);
    if (url) {
      return { type: "download", url, note: site ? from + ": plain file" : "", referrer: location.href, fileName: fileNameFor(el, url) };
    }
    if (isStream(el)) {
      // Facebook and X page titles describe the site, not the video.
      return { type: "stream", title: site ? "" : (document.title || "").trim().slice(0, 150), note: site ? from + ": page address" : "" };
    }
    const files = playerFilesFor(el);
    if (!files) return null;
    return { type: "download", url: files.url, audioUrl: files.audioUrl, note: from + ", " + files.note, referrer: location.href, fileName: fileNameFor(el, files.url) };
  }

  // Why nothing can be sent, for VeloDM's log: what this page has shown of its videos so far.
  function whyNothing() {
    const videos = [...document.querySelectorAll("video")].slice(0, 5).map((v) => {
      const src = v.currentSrc || v.src || "";
      return (src ? src.split(":")[0] : v.srcObject ? "object" : "no source") +
        (isFinite(v.duration) ? " " + Math.round(v.duration) + "s" : "") + (v.paused ? "" : " playing") + (v.mediaKeys ? " protected" : "");
    });
    const tags = [...new Set([...playerFiles.values()].slice(-8).map((f) => f.tag || "untagged"))].slice(0, 3);
    return `no file known for a video here; videos on the page: ${videos.join("; ") || "none"}` +
      (site === "fb" ? `; file server requests ${fbRequests}, video files ${playerFiles.size}${tags.length ? " (" + tags.join(", ") + ")" : ""}, listed files ${sniffed.size}` : "");
  }

  // The video a person most likely means on this page: the one with the pill, else the one
  // playing, else the biggest one in view.
  function likelyVideo() {
    if (current && current.isConnected) return current;
    const area = (v) => {
      const r = v.getBoundingClientRect();
      const inView = r.bottom > 0 && r.top < innerHeight && r.right > 0 && r.left < innerWidth;
      return inView ? r.width * r.height : 0;
    };
    return [...document.querySelectorAll("video")]
      .filter((v) => area(v) > 0)
      .sort((a, b) => (a.paused - b.paused) || area(b) - area(a))[0] || null;
  }

  // The background script asks this for "Download the video on this page" in the right-click menu.
  chrome.runtime.onMessage.addListener((message, _sender, reply) => {
    if (!message || message.type !== "pick") return false;
    const el = likelyVideo();
    reply((el && requestFor(el, "menu")) || { type: "none", note: "menu: " + whyNothing() });
    return false;
  });

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
    pillHost.setAttribute("data-velodm-pill", "");
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
    if (!alive()) {
      // The extension was updated or reloaded; a new copy of this script takes over the page.
      clearInterval(timer);
      current = null;
      if (pillHost) pillHost.remove();
      return;
    }
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
    const request = el && requestFor(el, "pill");
    if (!request) return;
    const original = label.textContent;
    const show = (text) => {
      label.textContent = text;
      setTimeout(() => {
        if (label) label.textContent = original;
      }, 2500);
    };
    try {
      chrome.runtime.sendMessage(request, (reply) =>
        show(!chrome.runtime.lastError && reply && reply.ok ? "Sent to VeloDM" : "VeloDM is not running"));
    } catch {
      // The extension was updated or reloaded after this page was opened.
      show("Reload this page first");
    }
  }

  // Clicking a magnet link hands it straight to VeloDM. If the app is not running, the link is
  // followed normally so whatever Windows has registered for magnet links still gets it.
  document.addEventListener("click", (e) => {
    if (!enabled || e.button !== 0 || e.defaultPrevented) return;
    const link = e.target instanceof Element ? e.target.closest("a[href]") : null;
    if (!link || !/^magnet:/i.test(link.href)) return;
    e.preventDefault();
    e.stopPropagation();
    try {
      chrome.runtime.sendMessage({ type: "download", url: link.href, referrer: location.href }, (reply) => {
        if (chrome.runtime.lastError || !reply || !reply.ok) location.href = link.href;
      });
    } catch {
      location.href = link.href; // the extension was reloaded after this page was opened
    }
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
