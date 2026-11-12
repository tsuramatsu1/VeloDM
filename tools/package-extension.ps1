# Builds the browser-extension packages that the stores accept, into dist\extension:
#   velodm-chromium-<version>.zip   Chrome Web Store, Microsoft Edge Add-ons (also Brave, Opera, Vivaldi)
#   velodm-firefox-<version>.zip    addons.mozilla.org
# The Firefox package is the same code with a manifest adapted for Firefox (see docs\PUBLISHING-EXTENSION.md).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'extension'
$output = Join-Path $root 'dist\extension'
New-Item -ItemType Directory -Force $output | Out-Null
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$manifestText = Get-Content (Join-Path $source 'manifest.json') -Raw -Encoding UTF8
$manifest = $manifestText | ConvertFrom-Json
$version = $manifest.version

function New-ExtensionPackage([string]$zipPath, [string]$manifestJson) {
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
    try {
        foreach ($file in Get-ChildItem $source -Recurse -File) {
            # Stores require forward slashes in entry names.
            $name = $file.FullName.Substring($source.Length + 1).Replace('\', '/')
            if ($name -eq 'manifest.json') { continue }
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $name) | Out-Null
        }
        $writer = New-Object System.IO.StreamWriter($zip.CreateEntry('manifest.json').Open(), (New-Object System.Text.UTF8Encoding($false)))
        try { $writer.Write($manifestJson) } finally { $writer.Dispose() }
    }
    finally {
        $zip.Dispose()
    }
    '{0}  ({1:N0} KB)' -f $zipPath, ((Get-Item $zipPath).Length / 1KB)
}

New-ExtensionPackage (Join-Path $output "velodm-chromium-$version.zip") $manifestText

# Firefox runs the background script as an event page, needs an add-on id, and asks every
# new add-on to declare what data leaves the browser (here: only to the VeloDM app on the same PC).
$manifest.background = [pscustomobject]@{ scripts = @('background.js') }
$manifest | Add-Member -NotePropertyName browser_specific_settings -NotePropertyValue ([pscustomobject]@{
    gecko = [pscustomobject]@{
        id = '{6f0c3c0e-7d1b-4f6e-9a55-2b8f6d1c9e41}'
        strict_min_version = '128.0'
        data_collection_permissions = [pscustomobject]@{ required = @('authenticationInfo', 'browsingActivity') }
    }
})
New-ExtensionPackage (Join-Path $output "velodm-firefox-$version.zip") ($manifest | ConvertTo-Json -Depth 10)
