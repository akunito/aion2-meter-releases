# AION2 DPS meter auto-start (A2M-18, 2026-10-10; clan option in Settings → Game since the clan build): runs hidden at logon,
# checks every 10 s; when AION2.exe is running and the meter is not, starts the meter. Closing it 60 s after the game ends is
# opt-in: settings.json "CloseWithGame": true (Diego 2026-10-11: by default the meter only opens by itself). The exe is looked up next to this script (deploy folder root) or one level up (shipped in data\).
# Register once (current user, no admin): powershell -ExecutionPolicy Bypass -File meter-autostart.ps1 -Register   (-Unregister removes it)
param([switch]$Register, [switch]$Unregister)
$here = Split-Path -Parent $PSCommandPath
$meter = Join-Path $here 'AION2DpsMeter.exe'
if (-not (Test-Path $meter)) { $meter = Join-Path (Split-Path -Parent $here) 'AION2DpsMeter.exe' }
$task = 'AION2 meter autostart'
if ($Unregister) {
    Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output 'removed'
    exit 0
}
if ($Register) {
    if (-not (Test-Path $meter)) { Write-Error "AION2DpsMeter.exe not found next to $PSCommandPath"; exit 2 }
    # conhost --headless: with Windows Terminal as the default console, powershell -WindowStyle Hidden still gets a MINIMISED
    # Terminal window (measured 2026-10-11 on DESK_W11); Hyper+T then picked that window over the real terminal, and closing it killed this loop.
    $action  = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $task -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
    Start-ScheduledTask -TaskName $task
    Write-Output 'registered + started'
    exit 0
}
$gone = 0
$settingsPath = Join-Path $env:APPDATA 'AionMeter\settings.json'
while ($true) {
    $closeWithGame = $false
    try { $cfg = Get-Content -Raw -ErrorAction Stop $settingsPath | ConvertFrom-Json; if ($cfg.CloseWithGame -eq $true) { $closeWithGame = $true } } catch { }
    $game = Get-Process -Name 'AION2' -ErrorAction SilentlyContinue
    $m = Get-Process -Name 'AION2DpsMeter' -ErrorAction SilentlyContinue
    if ($game) {
        $gone = 0
        if (-not $m -and (Test-Path $meter)) { Start-Process -FilePath $meter -WorkingDirectory (Split-Path $meter) }
    } elseif ($m -and $closeWithGame) {
        $gone += 10
        if ($gone -ge 60) { $m | ForEach-Object { $_.CloseMainWindow() | Out-Null }; Start-Sleep 5; Get-Process -Name 'AION2DpsMeter' -ErrorAction SilentlyContinue | Stop-Process; $gone = 0 }
    }
    Start-Sleep 10
}
