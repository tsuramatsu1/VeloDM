// VeloDM browser integration.
// Every http(s) download the browser starts is paused and offered to the VeloDM app.
// The app decides (by file type) whether to take it; if it declines or is not running,
// the browser download simply resumes.
// Streamed video is handed over as the address of the page it plays on, together with the
// stream playlist (.m3u8 / .mpd) the page loaded, for sites VeloDM's video tools do not know.
// Runs as a service worker in Chromium browsers and as an event page in Firefox.

const DEFAULTS = { enabled: true, port: 9615 };
const REQUEST_TIMEOUT_MS = 2500;

// Downloads we hand back to the browser must not be intercepted a second time.
const passThrough = new Set();

async function settings() {
  return chrome.storage.local.get(DEFAULTS);
}

async function callApp(path, body) {
  const { port } = await settings();
  const response = await fetch(`http://localhost:${port}${path}`, {
    method: body ? "POST" : "GET",
    // The header's value tells the app which version of the extension the browser is running.
    headers: { "Content-Type": "application/json", "X-VeloDM": chrome.runtime.getManifest().version },
    body: body ? JSON.stringify(body) : undefined,
    signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
  });
  if (!response.ok) throw new Error(`VeloDM replied ${response.status}`);
  return response.json();
}

async function cookieHeader(url) {
  try {
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.map((c) => `${c.name}=${c.value}`).join("; ");
  } catch {
    return "";
  }
}

function baseName(path) {
  return path ? path.split(/[\\/]/).pop() : "";
}

async function sendToApp({ url, referrer, fileName, mime, force }) {
  const result = await callApp("/add", {
    url,
    referrer: referrer || "",
    cookies: await cookieHeader(url),
    userAgent: navigator.userAgent,
    fileName: fileName || "",
    mime: mime || "",
    force: !!force,
  });
  return result.accepted === true;
}

// ---- stream playlists --------------------------------------------------------------------
// The first playlist a tab requests after a navigation is normally the master playlist.
const PLAYLIST = /\.(m3u8|mpd)([?#]|$)/i;
const playlists = new Map(); // tabId -> url; mirrored in session storage because this script is unloaded when idle
const sessionStore = chrome.storage.session;

async function playlistFor(tabId) {
  if (playlists.has(tabId)) return playlists.get(tabId);
  if (!sessionStore) return "";
  const key = `playlist:${tabId}`;
  return (await sessionStore.get(key).catch(() => ({})))[key] || "";
}

function forgetPlaylist(tabId) {
  playlists.delete(tabId);
  if (sessionStore) sessionStore.remove(`playlist:${tabId}`).catch(() => {});
}

if (chrome.webRequest) {
  chrome.webRequest.onBeforeRequest.addListener(
    (details) => {
      if (details.tabId < 0 || !PLAYLIST.test(details.url)) return;
      playlistFor(details.tabId).then((known) => {
        if (known) return; // later requests are the per-quality playlists
        playlists.set(details.tabId, details.url);
        if (sessionStore) sessionStore.set({ [`playlist:${details.tabId}`]: details.url }).catch(() => {});
      });
    },
    { urls: ["http://*/*", "https://*/*"], types: ["xmlhttprequest", "media", "other"] },
  );
  chrome.tabs.onUpdated.addListener((tabId, change) => {
    if (change.url) forgetPlaylist(tabId);
  });
  chrome.tabs.onRemoved.addListener(forgetPlaylist);
}

async function sendStreamToApp(tab, frameUrl, title) {
  // A player inside a frame is addressed by the frame's own page (e.g. an embedded player).
  const url = frameUrl || tab.url;
  if (!/^https?:/i.test(url || "")) return false;
  const result = await callApp("/add", {
    url,
    stream: true,
    title: title || "", // empty: VeloDM names the file after the video's own title
    manifestUrl: await playlistFor(tab.id),
    referrer: tab.url || url,
    cookies: await cookieHeader(url),
    userAgent: navigator.userAgent,
    force: true,
  });
  return result.accepted === true;
}

// onDeterminingFilename rather than onCreated: by this point the server has answered, so the
// real file name is known. Many file hosts use links with no extension in them and only name
// the file (e.g. "archive.rar") in the response, which onCreated cannot see yet.
// Firefox has no onDeterminingFilename; its onCreated already carries the target file name.
if (chrome.downloads.onDeterminingFilename) {
  chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
    // The browser holds the download at this point until suggest() is called, and only then
    // goes on to its own "Save as" window (when it is set to ask where to save each file).
    // So VeloDM is asked first: a download it takes is cancelled without that window ever
    // appearing. Calling suggest() with nothing keeps the browser's own file name.
    offerToApp(item).then(
      (taken) => {
        if (!taken) suggest();
      },
      () => suggest(),
    );
    return true; // suggest() is called later
  });
} else {
  chrome.downloads.onCreated.addListener((item) => {
    offerToApp(item);
  });
}

// Resolves to true when VeloDM took the download (and the browser's copy was cancelled).
async function offerToApp(item) {
  const url = item.finalUrl || item.url;
  if (!/^https?:/i.test(url)) return false; // blob:, data:, file: stay in the browser
  if (passThrough.delete(url)) return false;

  const { enabled } = await settings();
  if (!enabled) return false;

  // Pause first so the browser does not race ahead while we ask the app.
  await chrome.downloads.pause(item.id).catch(() => {});
  let taken = false;
  try {
    taken = await sendToApp({
      url,
      referrer: item.referrer,
      fileName: baseName(item.filename),
      mime: item.mime,
    });
  } catch {
    taken = false; // app not running or integration disabled
  }

  if (taken) {
    await chrome.downloads.cancel(item.id).catch(() => {});
    await chrome.downloads.erase({ id: item.id }).catch(() => {});
  } else {
    await chrome.downloads.resume(item.id).catch(() => {});
  }
  return taken;
}

function createMenus() {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: "velodm-link",
      title: "Download with VeloDM",
      contexts: ["link", "image", "video", "audio"],
    });
    chrome.contextMenus.create({
      id: "velodm-page-video",
      title: "Download the video on this page with VeloDM",
      contexts: ["page", "frame", "video"],
    });
  });
}

chrome.runtime.onInstalled.addListener(createMenus);
chrome.runtime.onStartup.addListener(createMenus);

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId === "velodm-page-video") {
    if (tab) await sendStreamToApp(tab, info.frameUrl, "").catch(() => false);
    return;
  }
  const url = info.linkUrl || info.srcUrl;
  if (!url || !/^(https?|magnet):/i.test(url)) return;
  try {
    await sendToApp({ url, referrer: info.pageUrl, force: true });
  } catch {
    // App unreachable: fall back to a normal browser download (not possible for magnet links).
    if (/^https?:/i.test(url)) {
      passThrough.add(url);
      chrome.downloads.download({ url });
    }
  }
});

chrome.runtime.onMessage.addListener((message, _sender, reply) => {
  if (message?.type === "ping") {
    callApp("/ping")
      .then((info) => reply({ ok: true, info }))
      .catch(() => reply({ ok: false }));
    return true; // async reply
  }
  if (message?.type === "download" && /^(https?|magnet):/i.test(message.url || "")) {
    // From the video pill or a clicked magnet link (content.js).
    sendToApp({ url: message.url, referrer: message.referrer, fileName: message.fileName, force: true })
      .then((accepted) => reply({ ok: accepted }))
      .catch(() => reply({ ok: false }));
    return true;
  }
  if (message?.type === "stream" && _sender.tab) {
    // From the video pill on a chunked stream (content.js).
    sendStreamToApp(_sender.tab, _sender.frameId ? _sender.url : "", message.title)
      .then((accepted) => reply({ ok: accepted }))
      .catch(() => reply({ ok: false }));
    return true;
  }
  return false;
});
