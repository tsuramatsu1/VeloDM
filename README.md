# VeloDM

A Windows download manager in the spirit of Internet Download Manager: multi-connection
downloads with dynamic segmentation, BitTorrent (magnet links and .torrent files), streamed video
(YouTube, HLS, DASH), pause/resume that survives restarts, a queue with a scheduler, speed limits,
file-type categories, clipboard monitoring, a tray icon, light and dark themes, and a browser
extension that hands downloads and videos to the app.

Built with .NET 10 and WPF. Third-party libraries:
[MonoTorrent](https://github.com/alanmcgovern/monotorrent) (BitTorrent) and
[SharpCompress](https://github.com/adamhathcock/sharpcompress) (archive extraction), both MIT.
Streamed video uses [yt-dlp](https://github.com/yt-dlp/yt-dlp), [FFmpeg](https://ffmpeg.org) and
[Deno](https://deno.com), separate programs that VeloDM downloads on request. See
`THIRD-PARTY-NOTICES.md`.

## Features

| Feature | How it works |
|---|---|
| **Multi-connection downloads** | Up to 32 HTTP range connections per file, each writing straight into a preallocated temp file at its own offset, so no merge step is needed. |
| **Dynamic segmentation** | Whenever a connection slot frees up, the segment with the most bytes left is split in half and the new half gets a fresh connection. Fast connections keep taking work from slow ones until the file is done (IDM's core trick). |
| **Pause / resume** | Segment state is saved to `%LocalAppData%\VeloDM\downloads.json`. Resume re-checks the server and continues only if the size is unchanged and ranges are still supported. |
| **Robustness** | Per-read 30 s stall timeout, automatic retries with back-off, fewer connections when the server answers 429/503, an immediate stop on permanent 4xx errors, and a single-connection fallback when the server ignores ranges or does not send a size. |
| **Queue + scheduler** | "Download Later" puts items in the queue. The queue runs N downloads at a time, can start and stop daily at set times, and can exit, sleep or shut down the PC when it finishes. |
| **Speed limiter** | A global token bucket shared by all connections (Downloads → Speed limiter), plus an optional limit per download (right-click → Speed limit for this download). |
| **Streamed video** | YouTube and most video sites, HLS and DASH streams: see [Streamed video](#streamed-video). |
| **Refresh expired links** | Right-click an unfinished download → **Refresh download address**: get a new link for the same file and the download carries on from where it stopped. See [Expired links](#expired-links). |
| **After a download** | Optionally extract archives (zip, rar, 7z, tar, gz) and scan the file for viruses (Windows Defender or your own scanner). Right-click → **Verify checksum** compares a file with the MD5/SHA checksum its site publishes. |
| **Proxy** | Options → Connection: use the Windows setting, no proxy, or your own HTTP/HTTPS/SOCKS5 proxy with optional login. |
| **Update check** | Looks for a newer release at startup and from Help → Check for updates. |
| **Dark theme** | Options → General → Appearance: Light, Dark, or the same as Windows. |
| **Log file** | `%LocalAppData%\VeloDM\logs\velodm.log` records what VeloDM did and why a download failed (Help → Open log folder). See [Troubleshooting](#troubleshooting). |
| **Categories** | Compressed, Documents, Music, Programs, Video and General, each saved to its own subfolder of your Downloads folder by default. |
| **Clipboard monitoring** | Copying a link to a captured file type opens the Add Download dialog. |
| **Browser integration** | The extension sends downloads, plus cookies, referrer and user agent so logged-in and protected links work, to the app on `localhost:9615`. It also adds "Download with VeloDM" to the right-click menu and a download pill on playing videos. |
| **BitTorrent** | Magnet links, `.torrent` URLs and local `.torrent` files, with DHT, trackers, peer exchange, local peer discovery and UPnP. Torrents share the same list, queue, scheduler and speed limiter as HTTP downloads. |
| **Progress window** | Per-download window. For HTTP it shows a live segment map and a connection table. For torrents it shows a piece map, seeds/peers and a file list where files can be unticked. |
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
| env `VELODM_DATA=<folder>` | Keeps settings, the download list and the video tools in that folder instead of `%LocalAppData%\VeloDM`, and runs as a separate instance (portable use, testing). |

## Streamed video

Video sites rarely serve a video as one file: the player fetches it in small pieces (HLS or DASH),
often with picture and sound separate. VeloDM hands these to free, open-source programs:
**yt-dlp** downloads the stream, **FFmpeg** joins picture and sound into one `.mp4`, and **Deno**
runs the scripts YouTube requires (a Node.js 22 or later that is already installed is used if Deno
is missing; without either, YouTube still works but may offer fewer qualities).

They are not part of VeloDM. The first time you download a streamed video, VeloDM asks, then
downloads them from their official GitHub release pages (about 250 MB) into
`%LocalAppData%\VeloDM\tools`; the yt-dlp download is checked against its published SHA-256.
**Options → Video** shows whether they are installed and updates them. If you already have
`yt-dlp.exe` and `ffmpeg.exe` on your `PATH`, those are used instead.

Video sites change often, so an old yt-dlp is the usual reason a video stops downloading. When a
video fails, its progress window offers **Update video tools and retry**, and the error says so
when the tools are more than two weeks old.

Streams are fetched several pieces at a time (the **Connections per download** setting, up to 16),
which makes HLS and DASH downloads many times faster than one piece at a time.

Ways to start a video download:

- the **Download this video** pill the browser extension shows on a playing video;
- right-click a page → **Download the video on this page with VeloDM**;
- **Tasks → Add video from a web page** and paste the page address. Pasting a page address into
  **Add URL** works too: VeloDM notices it is a page, not a file, and switches to video.

**Options → Video → Preferred quality** caps the resolution (best available by default). A video
download can be paused and resumed; finished parts are kept. If yt-dlp does not recognise a page,
VeloDM retries with the stream playlist (`.m3u8` / `.mpd`) the extension saw the page load.
Copy-protected (DRM) video, as on most paid streaming services, cannot be downloaded.

## Expired links

File hosts often hand out links that stop working after a while, so a paused download fails with
"403 Forbidden" when resumed. Right-click it → **Refresh download address**, then either:

1. click **Open the download page** and start the download there again: with the browser
   extension installed, VeloDM catches the new link and fills it in; or
2. paste a new link yourself.

VeloDM checks that the new link is the same file (same size, resumable) and continues from the
bytes already on disk. If it is a different file it says so before starting over.

## After a download

**Options → After download:**

- **Extract archives** unpacks zip, rar, 7z, tar and gzip files into a folder named after the
  archive, next to it. The archive is kept. Password-protected archives and the later volumes of a
  multi-part set are left alone. **Extract here** in the right-click menu does the same on demand.
- **Scan finished downloads for viruses** runs Windows Defender on the file (report only: it
  leaves the decision about a detected threat to Defender's own real-time protection), or the
  scanner command you enter, with `{file}` standing for the downloaded file. A flagged download is
  not extracted, and VeloDM shows a warning.

The result ("No threats found · Extracted to …") is shown in the download's Status column.

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

**Choosing files:** for a `.torrent` with several files, the Add dialog lists them with tick boxes.
For a magnet link the list is only known once the download has started, so untick files in the
progress window's file list then. The list can be changed at any time; ticking a file on a
finished torrent starts it downloading again. A piece shared with a neighbouring file is still
downloaded, so a small part of an unticked file can appear on disk.

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

## Browser extension (Chrome, Edge, Brave, other Chromium browsers, Firefox)

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

A captured download first shows the download dialog, with the name and the folder for its type
already filled in (`Downloads\Compressed`, `Downloads\Video`, `Downloads\Programs` and so on; change
the base folder in **Options → General**). Change them if you like, then choose **Start Download**
or **Download Later**. For a video, the dialog reads the page and fills in the video's title. To
start browser downloads straight away without the dialog, turn off **Options → Browser → Show the
download dialog for downloads from the browser**.

**Video pill:** when a video or audio file plays on a page, a small **Download this video** pill
appears at its top-right corner. Clicking it sends the file to VeloDM, named after the page title.
For media served as a plain file (for example an `.mp4` or `.webm` URL) that file is downloaded
directly. On **X (Twitter) and Facebook**, which stream their videos in chunks, the data they load
for each post also lists ordinary `.mp4` versions; the extension reads that list and offers the
best-quality one. On every other streaming site (YouTube and the like) the pill sends the page
address and VeloDM downloads the video with its video tools: see [Streamed video](#streamed-video).
This is also how **Facebook reels** are downloaded: the pill appears on a reel, a watch page or a
single post (pages whose address names one video), not in the news feed.
No pill is shown on copy-protected (DRM) video. The extension popup has a switch to turn the pill off.

**After upgrading VeloDM, reload the extension.** A browser keeps running the copy of an unpacked
extension it loaded earlier, so new extension features are missing until you open the extensions
page and press the reload button on VeloDM Integration (or restart the browser). VeloDM shows a
notification when the extension that calls it is older than the one it ships.

**Windows in front:** a download started from the browser opens its progress window (or the Add
dialog) in front of the browser, without taking the keyboard away from the page.

**Magnet links:** clicking a magnet link on a page sends it straight to VeloDM. If the app is not
running, the link is followed normally.

**File names:** the extension looks at a download after the server has answered, so links with no
file extension in them (common on file hosts) are still recognised by the real file name, such as
`archive.rar`.

**Firefox and the browser stores:** `tools\package-extension.ps1` builds store-ready packages for
the Chrome Web Store, Edge Add-ons and Firefox (the Firefox one has its own manifest). Publishing
needs your own developer accounts; `docs/PUBLISHING-EXTENSION.md` is the checklist, including how
to get a signed Firefox `.xpi` without listing it on the store. Until it is published, Firefox can
load the Firefox package temporarily from `about:debugging`. The Firefox build has not been run in
Firefox yet.

### Security of the local endpoint

The app listens on `http://localhost:<port>` only. Every request must carry an `X-VeloDM` header.
Web pages can only send that header after a CORS preflight, which the app never approves, so a web
page cannot queue a download. Requests that carry an `Origin` header are accepted only from
`chrome-extension://` or `moz-extension://` origins.

## Troubleshooting

**Help → Open log folder** opens `%LocalAppData%\VeloDM\logs`. `velodm.log` records, with times:

- every download the browser offered and whether VeloDM took it or left it to the browser, and why;
- downloads added, finished, stopped and failed, with the error;
- failed connections during a download;
- for video: when yt-dlp was started, its warnings and errors, and how it ended;
- after-download results, update checks, the browser extension's version, and unexpected errors.

Addresses are written without their query string, and cookies and passwords are never written,
because download links often carry access tokens. The log is limited to 2 MB plus one older file
(`velodm.1.log`).

## Project layout

```
src/VeloDM.Core     Engine: DownloadTask (HTTP segmentation), TorrentTask + TorrentService
                    (BitTorrent via MonoTorrent), StreamTask + VideoTools (streamed video via
                    yt-dlp), DownloadManager (queue, scheduler, persistence), HttpEngine (probe,
                    requests, proxy), PostProcessor (extract, virus scan), Checksums,
                    UpdateChecker, Log, SpeedLimiter, settings
src/VeloDM.App      WPF UI: main window, Add/Progress/Options/Batch dialogs, refresh-address,
                    checksum and video-tools dialogs, themes, tray icon, clipboard monitor,
                    BrowserBridge (localhost endpoint)
extension/          Manifest V3 browser extension
tests/VeloDM.Tests  Engine tests: a local range-capable HTTP server, plus a loopback BitTorrent
                    swarm (tracker + seeding engine) for .torrent and magnet downloads
tests/FakeYtDlp     Stand-in for yt-dlp, so the streamed-video tests need no internet or tools
installer/          WiX project and build.ps1 that produce the .msi installer
tools/IconGen       Draws the app icon: `dotnet run --project tools/IconGen` rewrites
                    src/VeloDM.App/Assets/VeloDM.ico and extension/icons/*.png
tools/package-extension.ps1   Builds the store packages of the extension into dist/extension
docs/               Publishing guide for the browser extension
```

Data lives in `%LocalAppData%\VeloDM` (`settings.json`, `downloads.json`). Partial files sit next
to their destination as `<name>.vdmpart` until they complete.

## Known limitations

- No FTP.
- Streamed video depends on yt-dlp, FFmpeg and Deno, which VeloDM downloads on request but does
  not include. DRM-protected video cannot be downloaded. A video download shows no segment map, and
  a changed speed limit applies to it only after a pause and resume.
- The video pill on Facebook reels relies on yt-dlp recognising the reel's address; it has not
  been verified on Facebook itself.
- Magnet links that list only WebTorrent (`wss://`) trackers rely on DHT to find peers.
- The speed limiter covers HTTP, torrents and video separately: each is held to the limit, so
  together they can use more than the limit.
- The proxy setting covers HTTP downloads and video, not BitTorrent traffic.
- Downloads behind POST forms may not resume after the link expires even with a refreshed address.
- The extension is not in the browser stores yet, so it is loaded unpacked (Chromium) or
  temporarily (Firefox). The Firefox build is untested in Firefox.
- The update check finds published GitHub releases of the repository in Options → General; until
  a release is published there, it reports that the current version is the latest.

## Licence

VeloDM is released under the MIT License: see `LICENSE`. Third-party software is listed in
`THIRD-PARTY-NOTICES.md`.
