# Third-party software

VeloDM is released under the MIT License (see `LICENSE`). It builds on the following software.

## Libraries shipped with VeloDM

| Library | Used for | Licence |
|---|---|---|
| [MonoTorrent](https://github.com/alanmcgovern/monotorrent) | BitTorrent | MIT — <https://github.com/alanmcgovern/monotorrent/blob/main/LICENSE.md> |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | Extracting rar, 7z, tar and gzip archives | MIT — <https://github.com/adamhathcock/sharpcompress/blob/master/LICENSE.txt> |
| [.NET runtime and WPF](https://github.com/dotnet) | Application runtime (bundled by the installer) | MIT — <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT> |

Each library remains under its own licence and copyright; the links above lead to the full texts.

## Programs VeloDM can download and run, but does not include

Streamed-video downloads use separate programs. They are not part of VeloDM and are not in
its installer: VeloDM downloads them from their official release pages only when you ask it to
(Options → Video).

| Program | Used for | Licence |
|---|---|---|
| [yt-dlp](https://github.com/yt-dlp/yt-dlp) | Reading video pages and downloading streams | The Unlicense (the Windows executable bundles components under other licences; see its repository) |
| [FFmpeg](https://ffmpeg.org) (builds from [yt-dlp/FFmpeg-Builds](https://github.com/yt-dlp/FFmpeg-Builds)) | Joining picture and sound | GPL v3 for the build VeloDM downloads |
| [Deno](https://github.com/denoland/deno) | Running the scripts YouTube requires, for yt-dlp | MIT |

You are responsible for using these tools, and the videos you download with them, in line with
the terms of the sites involved and the law where you live.
