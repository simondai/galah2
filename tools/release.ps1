<#
.SYNOPSIS
    Builds the NativeAOT green build, zips the four files, and publishes it as a GitHub release.

.DESCRIPTION
    The green build is a FOLDER (PublishAot and PublishSingleFile are mutually exclusive in
    .NET 8), so a GitHub release ships it as a zip: dist/Galah2-v<Version>-win-x64.zip.

    Pipeline:
      1. tools/publish-aot.ps1 (skippable with -SkipPublish to reuse artifact/)
      2. stage the four files into dist/stage/Galah2-v<Version>-win-x64/
         - the GalahData directory is NEVER zipped: it holds live user data (catalog,
           settings, ui-state). A fresh install has no GalahData; the app creates it on
           first start (AppRuntime -> AppDataPaths.EnsureCreated).
         - pdb files are never zipped (publish-aot.ps1 already deletes them; re-asserted here)
      3. Compress-Archive -> dist/Galah2-v<Version>-win-x64.zip
      4. gh release create v<Version> --notes-file <generated> [--draft]

    Prerequisites: the repo is pushed (the release is tagged on the current commit),
    gh CLI logged in (gh auth status), VS 2022 Build Tools with the C++ workload (AOT).

.PARAMETER Version
    The release version, e.g. 2.0.0. The tag becomes v2.0.0.

.PARAMETER Notes
    Release notes body (markdown). Defaults to a minimal template.

.PARAMETER Draft
    Create the release as a draft (visible only to repo collaborators).

.PARAMETER SkipPublish
    Reuse the current artifact/ instead of rebuilding (fast re-zip / re-tag).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/release.ps1 -Version 2.0.0

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/release.ps1 -Version 2.0.1 -SkipPublish -Draft
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = "",
    [switch]$Draft,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$artifact = Join-Path $root 'artifact'
$dist = Join-Path $root 'dist'
$stageRoot = Join-Path $dist 'stage'
$folderName = "Galah2-v$Version-win-x64"
$stage = Join-Path $stageRoot $folderName
$zip = Join-Path $dist "$folderName.zip"

Write-Host "version   : v$Version"
Write-Host "root      : $root"
Write-Host ""

if (-not $SkipPublish) {
    Write-Host "=== publish (AOT) ==="
    & powershell -ExecutionPolicy Bypass -File (Join-Path $root 'tools\publish-aot.ps1') -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw "publish-aot.ps1 failed" }
    Write-Host ""
}

# --- verify the artifact: exactly the four runtime files, no pdb, no data -------------
if (-not (Get-ChildItem $artifact -Filter *.exe -ErrorAction SilentlyContinue)) {
    throw "no exe in artifact/ - run without -SkipPublish first"
}
$files = @(Get-ChildItem $artifact -File | Where-Object { $_.Extension -ne '.pdb' })
Write-Host ("artifact  : {0} file(s)" -f $files.Count)
if ($files.Count -ne 4) { throw "expected the 4 green-build files (exe + 3 native dlls), found $($files.Count)" }

# --- stage + zip ------------------------------------------------------------------------
if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
foreach ($f in $files) { Copy-Item $f.FullName $stage }
# paranoia: never let a data directory into a public release
if (Test-Path (Join-Path $stage 'GalahData')) { throw "GalahData found in stage - aborting (live user data)" }

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
$zipMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ("zip       : {0}  ({1} MB)" -f $zip, $zipMb)
Write-Host ""

# --- github release ----------------------------------------------------------------------
$notesFile = Join-Path $dist "release-notes-v$Version.md"
if ([string]::IsNullOrWhiteSpace($Notes)) {
    $Notes = @"
Galah2 v$Version - win-x64 green build (NativeAOT, no runtime install needed).

**Run it:** unzip, open ``$folderName\Galah2.exe``. No installer, no .NET, no per-user
state outside the ``GalahData\`` folder next to the exe (created on first start).

- 4 files, ~43 MB (exe + Skia/HarfBuzz/GLES native libraries)
- License: GNU General Public License v3.0 (see LICENSE)
"@
}
Set-Content -Path $notesFile -Value $Notes -Encoding utf8

# gh writes progress to stderr; under $ErrorActionPreference='Stop' (Windows PowerShell 5.1)
# a stderr line from a native command throws NativeCommandError, so wrap every call.
function Invoke-Gh {
    param([Parameter(Position = 0)][string[]]$GhArgs)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $text = & gh @GhArgs 2>&1 | Out-String
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    return [pscustomobject]@{ Code = $code; Out = $text }
}

$tag = "v$Version"
$view = Invoke-Gh @('release', 'view', $tag)
if ($view.Code -eq 0) {
    Write-Host "tag $tag already has a release - uploading the zip to it"
    $up = Invoke-Gh @('release', 'upload', $tag, $zip, '--clobber')
    if ($up.Code -ne 0) { throw "gh release upload failed:`n$($up.Out)" }
} else {
    Write-Host "creating release $tag"
    $createArgs = @('release', 'create', $tag, $zip, '--title', "Galah2 v$Version - win-x64 green build", '--notes-file', $notesFile)
    if ($Draft) { $createArgs += '--draft' }
    $cr = Invoke-Gh $createArgs
    if ($cr.Code -ne 0) { throw "gh release create failed (is the current commit pushed? gh auth status?):`n$($cr.Out)" }
}
Write-Host ""
Write-Host "done. release: https://github.com/simondai/galah2/releases/tag/$tag"
