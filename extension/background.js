// VeloDM browser integration.
// Every http(s) download the browser starts is paused and offered to the VeloDM app.
// The app decides (by file type) whether to take it; if it declines or is not running,
// the browser download simply resumes.

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
    headers: { "Content-Type": "application/json", "X-VeloDM": "1" },
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

// onDeterminingFilename rather than onCreated: by this point the server has answered, so the
// real file name is known. Many file hosts use links with no extension in them and only name
// the file (e.g. "archive.rar") in the response, which onCreated cannot see yet.
chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
  suggest(); // keep the browser's own file name; we only want to look at it
  offerToApp(item);
});

async function offerToApp(item) {
  const url = item.finalUrl || item.url;
  if (!/^https?:/i.test(url)) return; // blob:, data:, file: stay in the browser
  if (passThrough.delete(url)) return;

  const { enabled } = await settings();
  if (!enabled) return;

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
}

function createMenus() {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: "velodm-link",
      title: "Download with VeloDM",
      contexts: ["link", "image", "video", "audio"],
    });
  });
}

chrome.runtime.onInstalled.addListener(createMenus);
chrome.runtime.onStartup.addListener(createMenus);

chrome.contextMenus.onClicked.addListener(async (info) => {
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
  return false;
});
