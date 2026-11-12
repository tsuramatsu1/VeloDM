const enabled = document.getElementById("enabled");
const videoPill = document.getElementById("videoPill");
const port = document.getElementById("port");
const status = document.getElementById("status");

function checkApp() {
  status.className = "";
  status.textContent = "Checking…";
  chrome.runtime.sendMessage({ type: "ping" }, (result) => {
    if (result?.ok) {
      status.className = "ok";
      status.textContent = "Connected to VeloDM";
    } else {
      status.className = "bad";
      status.textContent = "VeloDM is not running — downloads stay in the browser.";
    }
  });
}

chrome.storage.local.get({ enabled: true, videoPill: true, port: 9615 }, (s) => {
  enabled.checked = s.enabled;
  videoPill.checked = s.videoPill;
  port.value = s.port;
  checkApp();
});

enabled.addEventListener("change", () => chrome.storage.local.set({ enabled: enabled.checked }));
videoPill.addEventListener("change", () => chrome.storage.local.set({ videoPill: videoPill.checked }));
port.addEventListener("change", () => {
  const value = Number(port.value);
  if (value >= 1024 && value <= 65535) chrome.storage.local.set({ port: value }, checkApp);
});
