# Publishing the browser extension

Loading the extension unpacked works, but it needs Developer mode and Chromium browsers nag about
it. Publishing it to the browsers' stores removes both: users install it with one click and get
updates automatically. Publishing needs your own developer accounts, so it cannot be done from
this repository; this page is the checklist.

## 1. Build the packages

```powershell
.\tools\package-extension.ps1
```

This writes two zip files to `dist\extension`:

| File | For |
|---|---|
| `velodm-chromium-<version>.zip` | Chrome Web Store and Microsoft Edge Add-ons. Brave, Opera and Vivaldi install from the Chrome Web Store. |
| `velodm-firefox-<version>.zip` | addons.mozilla.org (Firefox 128 or later). |

The version is the `version` in `extension/manifest.json`. Raise it for every upload: the stores
reject a version they have already seen.

## 2. Where to upload

| Store | Developer console | Cost | Review |
|---|---|---|---|
| Chrome Web Store | <https://chrome.google.com/webstore/devconsole> | one-time US$5 registration | usually a few days; longer for broad host permissions |
| Microsoft Edge Add-ons | <https://partner.microsoft.com/dashboard/microsoftedge> | free | usually up to a week |
| Firefox Add-ons (AMO) | <https://addons.mozilla.org/developers/> | free | automated for most uploads, manual review may follow |

Firefox also offers **self-distribution**: upload the zip as "On your own", and AMO signs it and
hands back an `.xpi` you can host yourself (for example on the GitHub release). A signed `.xpi`
installs permanently by opening it in Firefox. Without signing, Firefox only loads the extension
temporarily (`about:debugging` → This Firefox → Load Temporary Add-on) until the browser restarts.

## 3. What the reviewers ask about

**Single purpose:** "Hands downloads, magnet links and videos from the browser to the VeloDM
download manager installed on the same computer."

**Permission justifications:**

| Permission | Why it is needed |
|---|---|
| `downloads` | Pause a download the browser starts, offer it to VeloDM, then cancel or resume it. |
| `contextMenus` | The "Download with VeloDM" and "Download the video on this page" right-click items. |
| `cookies` | Read the cookies of the address being downloaded so VeloDM can download files that need the user's login. |
| `storage` | The extension's own settings (on/off, port, video button). |
| `webRequest` | Notice the stream playlist (`.m3u8` / `.mpd`) a page loads, so a streamed video can be downloaded. Requests are only observed, never blocked or changed. |
| Host access to all sites | The download button on playing videos and the magnet-link handler run on every page; cookies are read for whichever site a download comes from. |

**Data use (the stores' privacy forms):** the extension sends the download address, referring
page, cookies for that address, the browser's user agent and the suggested file name to
`http://localhost:<port>`, the VeloDM app on the same PC. Nothing is sent to any server, nothing is
collected, sold or used for anything other than starting the download. Chrome's form calls the
relevant categories "authentication information" and "web history"; declare them as used for the
extension's single purpose and not transferred to third parties.

**Firefox data declaration:** the Firefox manifest built by the script declares
`data_collection_permissions` of `authenticationInfo` and `browsingActivity`, because cookies and
download addresses leave the browser (to the local app). AMO validates this field on upload; if it
reports an unknown value, adjust the list in `tools\package-extension.ps1` to the names its
validator gives.

**Remote code:** none. All scripts are in the package.

A privacy policy URL is required by the Chrome Web Store when cookies are read. A short page
repeating the "Data use" paragraph above is enough.

## 4. After it is published

- Put the store links in the README so users install from the store instead of loading unpacked.
- An extension installed from a store gets a different id from the unpacked one. VeloDM does not
  depend on the id: it accepts requests from any `chrome-extension://` or `moz-extension://`
  origin that sends the `X-VeloDM` header, so nothing in the app has to change.
- The Firefox add-on id in `tools\package-extension.ps1` must stay the same across versions, or
  Firefox treats an update as a different add-on.

## Differences in the Firefox package

The code is shared. The script changes only the manifest:

- `background.scripts` instead of `background.service_worker` (Firefox runs it as an event page).
- `browser_specific_settings.gecko` with the add-on id, the minimum Firefox version (128, the
  first to run page scripts in the `MAIN` world, which the X and Facebook video detection uses)
  and the data declaration.

Firefox has no `downloads.onDeterminingFilename`, so there the extension looks at a download when
it is created (`downloads.onCreated`) instead.

The Firefox package has been checked for valid structure only; it was not run in Firefox when
this was written. Load it once through `about:debugging` before submitting.
