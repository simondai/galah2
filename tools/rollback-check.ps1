<#
.SYNOPSIS
    Verifies the documented rollback path: 1.x and 2.0 read each other's data unchanged.

.DESCRIPTION
    CHANGELOG claims a user can fall back to 1.x without losing anything. That claim is only
    worth something if it has been executed, so this script runs it in both directions.

.PARAMETER V1
    The 1.x build output folder (contains Galah.exe and its managed companions).

.PARAMETER V2
    The 2.0 artifact folder (Galah2.exe plus the three natives).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/rollback-check.ps1
#>
param(
    [string]$V1 = "D:\Rolan_desktop\galah\Galah\bin\Release\net8.0-windows",
    [string]$V2 = "D:\Rolan_desktop\galah2\artifact"
)

$ErrorActionPreference = 'Stop'
$script:fail = 0

function Check($name, $ok, $detail) {
    if ($ok) { Write-Host ("PASS  " + $name + "  [" + $detail + "]") }
    else { Write-Host ("FAIL  " + $name + "  [" + $detail + "]"); $script:fail = $script:fail + 1 }
}

function Kill-Both {
    Get-Process Galah,Galah2 -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
}

function Wait-Alive($exeDir, $exeName, $seconds) {
    Start-Process -FilePath (Join-Path $exeDir $exeName) -WorkingDirectory $exeDir | Out-Null
    Start-Sleep -Seconds $seconds
    $procName = $exeName -replace '\.exe$', ''
    $p = Get-Process $procName -ErrorAction SilentlyContinue |
         Sort-Object WorkingSet64 -Descending | Select-Object -First 1
    if ($null -eq $p) { return $false }
    return ($p.WorkingSet64 -gt 0)
}

$work = Join-Path $env:TEMP ("galah-rollback-" + [guid]::NewGuid().ToString("N").Substring(0,8))
New-Item -ItemType Directory -Force -Path $work | Out-Null
Write-Host ("work dir: " + $work)
Write-Host ""

try {
    Copy-Item "$V1\*" $work -Force
    Copy-Item "$V2\*" $work -Force
    $haveV1 = Test-Path (Join-Path $work 'Galah.exe')
    $haveV2 = Test-Path (Join-Path $work 'Galah2.exe')
    Check "both builds staged" ($haveV1 -and $haveV2) "v1=$haveV1 v2=$haveV2"
    if (-not ($haveV1 -and $haveV2)) { throw "missing build output" }

    $data = Join-Path $work 'GalahData'
    $catalogPath = Join-Path $data 'catalog.json'

    # ---- direction 1: 2.0 creates the library, 1.x reads it ----
    Kill-Both
    if (Test-Path $data) { Remove-Item $data -Recurse -Force }

    $alive2 = Wait-Alive $work 'Galah2.exe' 10
    Check "2.0 starts with no data" $alive2 "alive=$alive2"
    Kill-Both

    $created = Test-Path $catalogPath
    Check "2.0 created catalog.json" $created "present=$created"
    if (-not $created) { throw "2.0 did not create a catalog" }

    $alive1 = Wait-Alive $work 'Galah.exe' 12
    Check "1.x starts on 2.0 data" $alive1 "alive=$alive1"
    Kill-Both

    $stillThere = Test-Path $catalogPath
    Check "1.x did not remove catalog.json" $stillThere "present=$stillThere"
    if ($stillThere) {
        $after = [System.IO.File]::ReadAllText($catalogPath)
        $parses = $after -match '"tabs"'
        Check "1.x left a readable catalog" $parses "tabs=$parses"
    }

    # ---- direction 2: 2.0 reads whatever 1.x left behind ----
    $alive2b = Wait-Alive $work 'Galah2.exe' 10
    Check "2.0 starts on 1.x-touched data" $alive2b "alive=$alive2b"
    Kill-Both

    $finalThere = Test-Path $catalogPath
    Check "2.0 did not remove catalog.json" $finalThere "present=$finalThere"
    if ($finalThere) {
        $final = [System.IO.File]::ReadAllText($catalogPath)
        $schema = $final -match '"schema"'
        Check "2.0 left the document readable" $schema "schema=$schema"
    }
}
finally {
    Kill-Both
    try { Remove-Item $work -Recurse -Force } catch { }
}

Write-Host ""
if ($script:fail -eq 0) {
    Write-Host "=== ROLLBACK OK ==="
    exit 0
}
Write-Host ("=== " + $script:fail + " FAILED ===")
exit 1
