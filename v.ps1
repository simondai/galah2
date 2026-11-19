$exe = 'D:\Rolan_desktop\galah2\src\Galah2\bin\Release\net8.0\Galah2.exe'
$dir = Split-Path $exe
Start-Process -FilePath $exe -WorkingDirectory $dir | Out-Null
Start-Sleep 9
$p = Get-Process Galah2 -EA SilentlyContinue | Where-Object { $_.WorkingSet64 -gt 0 } | Select-Object -First 1
if (-not $p) { 'NOT RUNNING'; exit }
'tray running, private = ' + [math]::Round($p.PrivateMemorySize64/1MB,1) + 'MB'

# Trigger settings through the tray menu is not scriptable, so call the same code path by
# toggling the panel first to prove the window stack still works.
$w = Start-Process -FilePath $exe -WorkingDirectory $dir -PassThru
$w.WaitForExit(6000) | Out-Null
Start-Sleep 6
$p = Get-Process Galah2 -EA SilentlyContinue | Where-Object { $_.WorkingSet64 -gt 0 } | Select-Object -First 1
'after toggle, private = ' + [math]::Round($p.PrivateMemorySize64/1MB,1) + 'MB'
