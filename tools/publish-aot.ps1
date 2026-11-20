<#
.SYNOPSIS
    Publishes the NativeAOT green build into artifact/ at the repository root.

.DESCRIPTION
    Produces the four files a portable build needs: Galah2.exe plus the three native libraries
    Skia, HarfBuzz and GLES require. PublishAot and PublishSingleFile are mutually exclusive in
    .NET 8, so this cannot be one file. See PORTABLE.md.

    The .pdb (175 MB of debug symbols) is removed after publishing.

.PARAMETER Configuration
    Release (default) or Debug.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/publish-aot.ps1
#>
param([string]$Configuration = "Release")

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$out = Join-Path $root 'artifact'

Write-Host "repo root : $root"
Write-Host "output    : $out"
Write-Host ""

Push-Location $root
try {
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }

    # AOT needs the MSVC linker. Fail early with a readable message.
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) {
        throw "vswhere.exe not found. NativeAOT needs VS 2022 Build Tools with the C++ workload."
    }
    $vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vsPath) {
        throw "VS found but the 'Desktop development with C++' workload is missing."
    }
    Write-Host "MSVC      : $vsPath"
    Write-Host ""

    dotnet publish src/Galah2 -c $Configuration -r win-x64 -p:PublishAot=true -o $out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

    Get-ChildItem $out -Filter *.pdb | Remove-Item -Force

    $files = Get-ChildItem $out -File
    $size = ($files | Measure-Object Length -Sum).Sum
    Write-Host ""
    Write-Host ("Artifact: " + $files.Count + " file(s), " + [math]::Round($size/1MB,1) + " MB")
    $files | Sort-Object Length -Descending | ForEach-Object {
        $mb = [math]::Round($_.Length / 1MB, 1)
        Write-Host ("  " + $_.Name.PadRight(28) + $mb.ToString().PadLeft(8) + " MB")
    }
    Write-Host ""
    Write-Host "Output folder: $out"
    Write-Host "Copy the whole folder. Copying only the exe will not start (missing 3 native DLLs)."
}
finally { Pop-Location }
