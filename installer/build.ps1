# Builds installer\output\VeloDM-<version>-x64.msi.
# The app is published self-contained, so the target PC does not need .NET installed.
# WiX comes from NuGet (WixToolset.Sdk), so nothing has to be installed to run this.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $PSScriptRoot 'publish'
$appProject = Join-Path $root 'src\VeloDM.App\VeloDM.App.csproj'

$version = ([xml](Get-Content $appProject -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $appProject" }

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $appProject -c Release -r win-x64 --self-contained true -o $publish -p:DebugType=none --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

dotnet build (Join-Path $PSScriptRoot 'VeloDM.Installer.wixproj') -c Release "-p:ProductVersion=$version" --nologo
if ($LASTEXITCODE -ne 0) { throw 'The installer build failed' }

Get-ChildItem (Join-Path $PSScriptRoot 'output') -Filter *.msi -Recurse |
    ForEach-Object { '{0}  ({1:N1} MB)' -f $_.FullName, ($_.Length / 1MB) }
