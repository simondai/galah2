<#
.SYNOPSIS
    Measures Galah2's memory the way the acceptance criteria define it.

.DESCRIPTION
    Reports Private Working Set — the number Task Manager's "Memory" column shows.
    Working Set (which includes shared pages and therefore looks much larger) is reported
    separately and must never be quoted as the app's own memory.

    Operating sequence is fixed so runs are comparable:
      1. start the app, let it settle (tray only)
      2. launch a second instance to toggle the panel open
      3. hide again, wait, measure

.PARAMETER Exe
    Path to the build under test. Defaults to the normal managed Release build.

.PARAMETER Cycles
    How many open/close cycles to run. Default 2.
#>
param(
    [string]$Exe = "",
    [int]$Cycles = 2,
    [int]$SettleSeconds = 8
)

# Resolve the default from the repository root instead of a developer machine's absolute path.
# The solution builds Release|x64, so the managed build lands in bin/x64/Release/net8.0/.
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $Exe = Join-Path $repoRoot "src\Galah2\bin\x64\Release\net8.0\Galah2.exe"
}

if (-not (Test-Path $Exe)) { throw "not found: $Exe (pass -Exe <path> to select a build)" }

$dir = Split-Path -Parent $Exe
$name = [System.IO.Path]::GetFileNameWithoutExtension($Exe)

function Get-Snapshot([string]$label) {
    $p = Get-Process -Name $name -ErrorAction SilentlyContinue |
         Where-Object { $_.WorkingSet64 -gt 0 } | Select-Object -First 1
    if (-not $p) { Write-Host ("{0,-16} (not running)" -f $label); return $null }

    $privateWs = $null
    try {
        $sample = (Get-Counter "\Process($name)\Working Set - Private" -ErrorAction Stop).CounterSamples[0]
        if ($sample) { $privateWs = $sample.CookedValue }
    } catch { }

    if (-not $privateWs) { $privateWs = $p.PrivateMemorySize64 }

    [pscustomobject]@{
        Label     = $label
        PrivateMB = [math]::Round($privateWs / 1MB, 1)
        TotalMB   = [math]::Round($p.WorkingSet64 / 1MB, 1)
        Handles   = $p.HandleCount
        Threads   = $p.Threads.Count
    }
}

function Show($s) {
    if ($null -eq $s) { return }
    Write-Host ("{0,-16} PrivateWS={1,7:N1} MB   WorkingSet={2,7:N1} MB   handles={3} threads={4}" -f `
        $s.Label, $s.PrivateMB, $s.TotalMB, $s.Handles, $s.Threads)
}

Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

$results = @()
Write-Host "exe: $Exe"
Write-Host ""

Start-Process -FilePath $Exe -WorkingDirectory $dir | Out-Null
Start-Sleep -Seconds $SettleSeconds
$results += Get-Snapshot "tray (idle)"

for ($i = 1; $i -le $Cycles; $i++) {
    # a second instance forwards the toggle and exits
    $w = Start-Process -FilePath $Exe -WorkingDirectory $dir -PassThru
    $w.WaitForExit($SettleSeconds * 1000) | Out-Null
    Start-Sleep -Seconds 5
    $results += Get-Snapshot "panel open #$i"

    $w = Start-Process -FilePath $Exe -WorkingDirectory $dir -PassThru
    $w.WaitForExit($SettleSeconds * 1000) | Out-Null
    Start-Sleep -Seconds 12
    $results += Get-Snapshot "panel hidden #$i"
}

$results | Where-Object { $_ } | ForEach-Object { Show $_ }

Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host ""
Write-Host "NOTE: quote PrivateWS only. WorkingSet includes shared DLL pages."
