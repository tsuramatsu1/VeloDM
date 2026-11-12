// Runs inside the page itself (world: MAIN) on X/Twitter and Facebook only.
// Those sites play video as chunked streams, but the data they load for each post also lists
// ordinary .mp4 versions of the same video. This script reads the site's own API responses,
// picks out those .mp4 links and passes them to content.js, which shows the download pill.
// Nothing is sent anywhere else, and no request is changed.

(() => {
  if (window.__velodmHook) return;
  window.__velodmHook = true;

  const site = /(^|\.)facebook\.com$/.test(location.hostname) ? "fb" : "x";

  // content.js starts after this script, so everything found is kept and sent again when it asks.
  const found = [];
  function post(items) {
    if (!items.length) return;
    found.push(...items);
    if (found.length > 3000) found.splice(0, found.length - 3000);
    window.postMessage({ velodm: "media", site, items }, location.origin);
  }
  window.addEventListener("message", (e) => {
    if (e.source === window && e.data && e.data.velodm === "replay" && found.length) {
      window.postMessage({ velodm: "media", site, items: found }, location.origin);
    }
  });

  // X: every video has several "variants"; the .mp4 ones carry the media id and size in the path,
  // e.g. https://video.twimg.com/ext_tw_video/<id>/pu/vid/avc1/1280x720/<name>.mp4
  function scanX(text) {
    const items = [];
    const flat = text.replaceAll("\\/", "/");
    for (const m of flat.matchAll(/https:\/\/video\.twimg\.com\/[^"\\\s]+?\.mp4[^"\\\s]*/g)) {
      const id = (m[0].match(/\/(\d{8,})\//) || [])[1];
      if (!id) continue;
      const size = m[0].match(/\/(\d+)x(\d+)\//);
      items.push({ keys: [id], url: m[0], quality: size ? size[1] * size[2] : 0 });
    }
    return items;
  }

  // Facebook: each video record holds a DASH manifest (the chunked stream the player uses) next to
  // "progressive" .mp4 links. The manifest's file paths are the keys: content.js sees the player
  // request those paths and so knows which record is playing.
  const FB_TOKEN = new RegExp(
    '"(browser_native_hd_url|browser_native_sd_url|playable_url_quality_hd|playable_url|progressive_url)"\\s*:\\s*"(https:[^"]+)"' +
    "|<BaseURL>(https:[^<]+)</BaseURL>", "g");

  function scanFb(text) {
    if (!/progressive_url|browser_native_|playable_url/.test(text)) return [];
    const flat = text
      .replace(/\\u([0-9a-fA-F]{4})/g, (_, hex) => String.fromCharCode(parseInt(hex, 16)))
      .replaceAll("\\/", "/");

    const tokens = [];
    for (const m of flat.matchAll(FB_TOKEN)) {
      if (m[3]) {
        try {
          tokens.push({ path: new URL(m[3].replaceAll("&amp;", "&")).pathname });
        } catch {}
      } else {
        let quality = /hd/.test(m[1]) ? 2 : 1;
        if (m[1] === "progressive_url") {
          const q = flat.slice(m.index, m.index + m[0].length + 200).match(/"quality"\s*:\s*"(\w+)"/);
          quality = q && q[1] === "HD" ? 2 : 1;
        }
        tokens.push({ url: m[2], quality });
      }
    }

    // Split into records: a run of manifest paths plus the run of links beside it.
    const pathsFirst = tokens.length > 0 && "path" in tokens[0];
    const records = [];
    let record = null;
    let last = "";
    for (const t of tokens) {
      const kind = "path" in t ? "path" : "url";
      const startsRecord = kind === (pathsFirst ? "path" : "url");
      if (!record || (kind !== last && startsRecord)) {
        record = { keys: [], links: [] };
        records.push(record);
      }
      if (kind === "path") record.keys.push(t.path);
      else record.links.push(t);
      last = kind;
    }

    const items = [];
    for (const r of records) {
      for (const link of r.links) items.push({ keys: r.keys, url: link.url, quality: link.quality });
    }
    return items;
  }

  function scan(text) {
    if (typeof text !== "string" || text.length < 40) return;
    try {
      post(site === "x" ? scanX(text) : scanFb(text));
    } catch {}
  }

  const interesting = (url) => /graphql|\/api\/|\/1\.1\/|\/2\//.test(url || "");

  const originalFetch = window.fetch;
  window.fetch = function (...args) {
    const promise = originalFetch.apply(this, args);
    promise
      .then((response) => {
        if (interesting(response.url)) response.clone().text().then(scan).catch(() => {});
      })
      .catch(() => {});
    return promise;
  };

  const originalOpen = XMLHttpRequest.prototype.open;
  XMLHttpRequest.prototype.open = function (method, url, ...rest) {
    this.addEventListener("load", () => {
      try {
        if (!interesting(String(url))) return;
        if (this.responseType === "" || this.responseType === "text") scan(this.responseText);
        else if (this.responseType === "json") scan(JSON.stringify(this.response));
      } catch {}
    });
    return originalOpen.call(this, method, url, ...rest);
  };

  // The first videos on a page arrive inside the HTML itself, in inline scripts, rather than
  // through an API call. Some of those scripts remove themselves once they have run, so they
  // are read as the parser adds them instead of after the page has loaded.
  const marker = site === "x" ? ".mp4" : "_url";
  function scanScript(script) {
    if (!script || script.tagName !== "SCRIPT" || script.src) return;
    const text = script.textContent;
    if (text.length > 200 && text.includes(marker)) scan(text);
  }
  new MutationObserver((mutations) => {
    for (const m of mutations) {
      for (const node of m.addedNodes) {
        if (node.nodeType === Node.TEXT_NODE) scanScript(node.parentNode);
        else if (node.nodeType === Node.ELEMENT_NODE) scanScript(node);
      }
    }
  }).observe(document, { childList: true, subtree: true });
})();
