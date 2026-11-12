# VeloDM

A Windows download manager in the spirit of Internet Download Manager: multi-connection
downloads with dynamic segmentation, BitTorrent (magnet links and .torrent files), pause/resume
that survives restarts, a queue with a scheduler, a global speed limiter, file-type categories,
clipboard monitoring, a tray icon and a Chrome/Edge extension that hands browser downloads to
the app.

Built with .NET 10 and WPF. The only third-party dependency is
[MonoTorrent](https://github.com/alanmcgovern/monotorrent) (MIT), used for BitTorrent.

## Features

| Feature | How it works |
|---|---|
| **Multi-connection downloads** | Up to 32 HTTP range connections per file, each writing straight into a preallocated temp file at its own offset, so no merge step is needed. |
| **Dynamic segmentation** | Whenever a connection slot frees up, the segment with the most bytes left is split in half and the new half gets a fresh connection. Fast connections keep taking work from slow ones until the file is done (IDM's core trick). |
| **Pause / resume** | Segment state is saved to `%LocalAppData%\VeloDM\downloads.json`. Resume re-checks the server and continues only if the size is unchanged and ranges are still supported. |
| **Robustness** | Per-read 30 s stall timeout, automatic retries with back-off, fewer connections when the server answers 429/503, an immediate stop on permanent 4xx errors, and a single-connection fallback when the server ignores ranges or does not send a size. |
| **Queue + scheduler** | "Download Later" puts items in the queue. The queue runs N downloads at a time, can start and stop daily at set times, and can exit, sleep or shut down the PC when it finishes. |
| **Speed limiter** | A global token bucket shared by all connections (Downloads → Speed limiter). |
| **Categories** | Compressed, Documents, Music, Programs, Video and General, each saved to its own subfolder of your Downloads folder by default. |
| **Clipboard monitoring** | Copying a link to a captured file type opens the Add Download dialog. |
| **Browser integration** | The extension sends downloads, plus cookies, referrer and user agent so logged-in and protected links work, to the app on `localhost:9615`. It also adds "Download with VeloDM" to the right-click menu and a download pill on playing videos. |
| **BitTorrent** | Magnet links, `.torrent` URLs and local `.torrent` files, with DHT, trackers, peer exchange, local peer discovery and UPnP. Torrents share the same list, queue, scheduler and speed limiter as HTTP downloads. |
| **Progress window** | Per-download window. For HTTP it shows a live segment map and a connection table. For torrents it shows a piece map, seeds/peers and a file list. |
| **Tray + single instance** | Closing the window leaves VeloDM in the tray. Running `VeloDM.exe <url>` while it is open hands the URL to the running instance. |
| **Start with Windows** | **Options → General → Start VeloDM when I sign in to Windows** starts it hidden in the tray at sign-in, so browser capture and the scheduler are always ready. It is off by default and is a per-user setting (a value under the HKCU `Run` key), so it also shows up in Task Manager's Startup apps. |

## Build and run

```powershell
dotnet build VeloDM.slnx -c Release
.\src\VeloDM.App\bin\Release\net10.0-windows\VeloDM.exe
```

Run the engine tests (they use a local HTTP server, so no internet is needed):

```powershell
dotnet test
```

Build the installer:

```powershell
.\installer\build.ps1
```

This writes `installer\output\VeloDM-<version>-x64.msi`. The app is published self-contained, so
the target PC does not need .NET. WiX is restored from NuGet, so nothing needs installing first.
The installer puts VeloDM in `Program Files\VeloDM` (it asks for admin rights), adds Start menu and
desktop shortcuts, registers VeloDM for magnet links and `.torrent` files, and starts the app on
its browser-extension setup window. A newer `.msi` upgrades an older install in place. Uninstall
from **Settings → Apps**. For a silent install: `msiexec /i VeloDM-<version>-x64.msi /qn`.

The version comes from `<Version>` in `src/VeloDM.App/VeloDM.App.csproj`. Raise it before building
an installer meant to upgrade an existing install.

Command-line options:

| Option | Effect |
|---|---|
| `VeloDM.exe <url>` | Opens the Add Download dialog for the URL (forwards it to an already-running instance). |
| `--tray` | Starts hidden in the tray. The start-with-Windows option uses this. |
| `--software-render` (or env `VELODM_SOFTWARE=1`) | Forces WPF software rendering, for VMs or remote sessions where windows show up blank. |

## BitTorrent

Three ways to add a torrent:

- **Magnet link:** paste it into **Add URL**, copy it (clipboard monitoring picks it up), right-click it
  in the browser and choose **Download with VeloDM**, or click it once VeloDM is the magnet handler.
- **`.torrent` file:** use the **Torrent** toolbar button (Ctrl+T), double-click the file, or run
  `VeloDM.exe path\to\file.torrent`.
- **`.torrent` URL:** add it like any URL. The browser extension captures `.torrent` downloads, and
  their cookies are passed along, so private-tracker links work.

Torrents save to `Downloads\Torrents` by default. The Add dialog shows the name, size and file count
before you start. It refuses a torrent that's already in the list, matching on info-hash.

**Options → BitTorrent:**

- listening port (default 51515)
- DHT and UPnP/NAT-PMP port forwarding
- upload limit
- whether to keep seeding after the download finishes, up to a share ratio
- **Open magnet links and .torrent files with VeloDM**, which registers the handlers under HKCU, so no
  admin rights are needed

How it works: one MonoTorrent engine is created the first time a torrent runs, so VeloDM opens no
listening port and joins no DHT until you actually download a torrent. Pausing saves fast-resume
data, so resuming skips the full hash check. MonoTorrent verifies every piece by hash. A finished
torrent that is still seeding shows "Seeding (ratio x.xx)". Pausing it stops seeding.

## Browser extension (Chrome, Edge, Brave, other Chromium browsers)

Chromium browsers do not let an installer add an extension from outside their store, so the last
step is manual. **Help → Install browser extension** makes it short: it lists the browsers it
finds, opens each one's extensions page, and copies the extension folder path to the clipboard.
The installer opens this window after a first install. The steps it walks through are:

1. Open `chrome://extensions` (or `edge://extensions`).
2. Turn on **Developer mode**.
3. Click **Load unpacked** and select the `extension` folder, which sits next to `VeloDM.exe`.

How it works: every http(s) download the browser starts is paused and offered to VeloDM. The app
accepts it only if the file type is in **Options → Browser → Capture these file types**. Otherwise,
or when the app isn't running, the browser download simply resumes. Right-click → **Download with
VeloDM** always sends the link, whatever its type.

A captured download starts at once and is saved by type: `Downloads\Compressed`, `Downloads\Video`,
`Downloads\Programs` and so on (change the base folder in **Options → General**). To be asked for
the name and folder each time instead, turn on **Options → Browser → Ask before starting a download
captured from the browser**.

**Video pill:** when a video or audio file plays on a page, a small **Download this video** pill
appears at its top-right corner. Clicking it sends the file to VeloDM, named after the page title.
The pill appears for media served as a plain file (for example an `.mp4` or `.webm` URL), and on
**X (Twitter) and Facebook**. Those two stream their videos in chunks, but the data they load for
each post also lists ordinary `.mp4` versions; the extension reads that list on those sites and
offers the best-quality one. Other streaming sites, such as YouTube, are not covered: they have no
single file to download. The extension popup has a switch to turn the pill off.

**Magnet links:** clicking a magnet link on a page sends it straight to VeloDM. If the app is not
running, the link is followed normally.

**File names:** the extension looks at a download after the server has answered, so links with no
file extension in them (common on file hosts) are still recognised by the real file name, such as
`archive.rar`.

### Security of the local endpoint

The app listens on `http://localhost:<port>` only. Every request must carry an `X-VeloDM` header.
Web pages can only send that header after a CORS preflight, which the app never approves, so a web
page cannot queue a download. Requests that carry an `Origin` header are accepted only from
`chrome-extension://` or `moz-extension://` origins.

## Project layout

```
src/VeloDM.Core     Engine: DownloadTask (HTTP segmentation), TorrentTask + TorrentService
                    (BitTorrent via MonoTorrent), DownloadManager (queue, scheduler,
                    persistence), HttpEngine (probe + requests), SpeedLimiter, settings
src/VeloDM.App      WPF UI: main window, Add/Progress/Options/Batch dialogs, tray icon,
                    clipboard monitor, BrowserBridge (localhost endpoint)
extension/          Manifest V3 browser extension
tests/VeloDM.Tests  Engine tests: a local range-capable HTTP server, plus a loopback BitTorrent
                    swarm (tracker + seeding engine) for .torrent and magnet downloads
installer/          WiX project and build.ps1 that produce the .msi installer
tools/IconGen       Draws the app icon: `dotnet run --project tools/IconGen` rewrites
                    src/VeloDM.App/Assets/VeloDM.ico and extension/icons/*.png
```

Data lives in `%LocalAppData%\VeloDM` (`settings.json`, `downloads.json`). Partial files sit next
to their destination as `<name>.vdmpart` until they complete.

## Known limitations

- HTTP/HTTPS and BitTorrent only. There is no FTP or HLS/DASH stream grabbing.
- Torrents always download every file. There is no per-file selection yet. Magnet links that list
  only WebTorrent (`wss://`) trackers rely on DHT to find peers.
- The speed limiter covers HTTP and torrents separately: each is held to the limit, so together they
  can use up to twice the limit.
- Downloads behind POST forms or one-time tokens may not resume after the link expires. Use
  **Redownload** with a fresh link.
- The extension targets Chromium browsers. Firefox needs a `background.scripts` manifest variant.
