# TylerCast Windows test round 4: the tray app itself. Run from PowerShell with the one line on the round page:
#   irm <page>/round.ps1 | iex
# Round 3 showed the app casting well, but the first cast failed on Windows' firewall question
# (now: the app waits up to 45s for it), and Fit to TV, the menu and Settings went unchecked.
# This copy lives in a new folder, so Windows asks the firewall question again: a real test of the fix.
# PowerShell 5.1 compatible; plain ASCII.

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
$Base = '__BASE__'
$Work = Join-Path ([Environment]::GetFolderPath('Desktop')) 'TylerCast-round4'
$Results = Join-Path $Work 'results'
New-Item -ItemType Directory -Force -Path $Results | Out-Null
Set-Location $Work
Start-Transcript -Path (Join-Path $Results 'console.txt') -Force | Out-Null
$Answers = Join-Path $Results 'answers.txt'
"TylerCast round 4, $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" | Set-Content $Answers
$Data = Join-Path $env:LOCALAPPDATA 'TylerCast'
$Log = Join-Path $Data 'TylerCast.log'
$Settings = Join-Path $Data 'settings.json'
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Say($text) { Write-Host ''; Write-Host $text -ForegroundColor Cyan }
function Note($k, $v) { "$k = $v" | Add-Content $Answers }
function Ask($question, $key) {
    $a = ''
    while ($a -notin @('y', 'n', 's')) {
        $a = (Read-Host "$question  [y = yes, n = no, s = not sure]").Trim().ToLower()
    }
    Note $key $a
    return $a
}
function AskText($question, $key) {
    $a = Read-Host $question
    Note $key $a
    return $a
}
function Step($n, $total, $text) { Write-Host ''; Write-Host "---- Step $n of $total : $text ----" -ForegroundColor Yellow }
function StopApp() { Get-Process -Name 'TylerCast' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue }

$exe = Join-Path $Work 'TylerCast.exe'
$total = 7

# ------------------------------------------------------------------ power, download, fresh start
Step 1 $total 'Power and download'
$power = (Get-CimInstance -ClassName Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1)
if ($power -and $power.BatteryStatus -eq 1) {
    Say 'The laptop is on battery. Please plug it in: on battery Windows slows it down.'
    Read-Host 'Press Enter once it is plugged in (or to carry on anyway)' | Out-Null
}
$power = (Get-CimInstance -ClassName Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1)
if ($power) { Note 'battery_status' "$($power.BatteryStatus) (1 = on battery, 2 = plugged in)" }
StopApp
try {
    Invoke-WebRequest "$Base/files/TylerCast.exe" -OutFile $exe -UseBasicParsing
    Write-Host "Downloaded $([math]::Round((Get-Item $exe).Length / 1MB)) MB"
} catch {
    Write-Host "Download failed: $_" -ForegroundColor Red
    Write-Host 'Is the Mac still serving the round page? Try opening the page again.'
    Stop-Transcript | Out-Null
    return
}
$logFrom = if (Test-Path $Log) { (Get-Content $Log).Count } else { 0 }
$profiles = Get-NetConnectionProfile -ErrorAction SilentlyContinue
foreach ($p in $profiles) { Note 'network_profile' "$($p.InterfaceAlias): $($p.NetworkCategory)" }
# The flyout drawn on this laptop (its fonts and scaling), every state, light and dark.
& $exe --screenshot (Join-Path $Results 'screens') | Out-Null

# ------------------------------------------------------------------ start
Step 2 $total 'Start TylerCast'
Read-Host 'Press Enter to start TylerCast (its panel opens by the clock)' | Out-Null
Start-Process $exe
Start-Sleep -Seconds 3

# ------------------------------------------------------------------ cast
Step 3 $total 'Cast to the TV from the panel'
Say @'
Switch the TV on (normal TV or HDMI input, not standby). Start a YouTube video with
movement and sound, and leave it playing.

Then open the TylerCast panel. The TV should be listed (it can take a few seconds).
Click the TV, leave "Sound on TV" and "Mute this PC" ticked, and click Cast.
If Windows asks whether TylerCast can use the network: tick Private networks, click Allow.
'@
Ask 'Was the TV listed in the panel?' 'tv_listed' | Out-Null
Ask 'Did Windows ask about the network (a firewall window)?' 'firewall_prompt' | Out-Null
Ask 'While Windows was asking, did the panel say it was waiting for Windows?' 'waiting_shown' | Out-Null
Ask 'Is the laptop''s screen on the TV now, on the first try?' 'cast_seen' | Out-Null
Ask 'Is the sound coming from the TV (and not the laptop)?' 'cast_sound' | Out-Null

# ------------------------------------------------------------------ while casting
Step 4 $total 'Fit to TV, and the panel while casting'
Say @'
Open the panel. The TV's row should say "Casting" with a timer, and show a
"Fit to TV" slider. Drag the slider until the edges of the screen are no longer cut
off on the TV.
'@
Ask 'Did the picture on the TV shrink as you moved the slider?' 'fit_live' | Out-Null
Ask 'Can you now see all four edges of the screen on the TV?' 'fit_edges' | Out-Null
Say 'Close the panel, then rest the mouse pointer on the TylerCast icon without clicking.'
Ask 'Did a tooltip say it is casting, with the time?' 'tooltip' | Out-Null

# ------------------------------------------------------------------ settings, menu
Step 5 $total 'Right-click menu and settings'
Say @'
Right-click the TylerCast icon. A menu should offer Stop casting, Settings, Open log and Exit.
Click Settings. Turn "Start with Windows" on, then off again. Then close the Settings window.
'@
Ask 'Did the right-click menu appear with those four items?' 'menu' | Out-Null
Ask 'Did the Settings window open, and did the switch turn on and off?' 'settings' | Out-Null

# ------------------------------------------------------------------ disconnect, looks
Step 6 $total 'Disconnect'
Say 'Open the panel and click Disconnect.'
Ask 'Did the TV stop showing the laptop (back to its own picture)?' 'disconnect' | Out-Null
Ask 'Did the laptop''s own sound come back?' 'sound_back' | Out-Null
AskText 'Anything that looked wrong, or felt awkward? (Enter to skip)' 'notes' | Out-Null

# ------------------------------------------------------------------ exit, send back
Step 7 $total 'Exit and send the results to the Mac'
Say 'Right-click the TylerCast icon and click Exit.'
Read-Host 'Press Enter once you have clicked Exit' | Out-Null
Start-Sleep -Seconds 2
$still = Get-Process -Name 'TylerCast' -ErrorAction SilentlyContinue
Note 'exited' $(if ($still) { 'no (still running; stopped by the script)' } else { 'yes' })
StopApp
# Leave nothing behind from the test: no Start with Windows entry.
$run = Get-ItemProperty -Path $RunKey -Name 'TylerCast' -ErrorAction SilentlyContinue
Note 'start_with_windows_left_on' $(if ($run) { "yes: $($run.TylerCast)" } else { 'no' })
if ($run) { Remove-ItemProperty -Path $RunKey -Name 'TylerCast' -ErrorAction SilentlyContinue }

Stop-Transcript | Out-Null
if (Test-Path $Log) { Get-Content $Log | Select-Object -Skip $logFrom | Set-Content (Join-Path $Results 'TylerCast.log') }
if (Test-Path $Settings) { Copy-Item $Settings (Join-Path $Results 'settings.json') -Force }
$zip = Join-Path $Work 'results-round4.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $Results '*') -DestinationPath $zip -Force
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Sending $mb MB to the Mac..."
try {
    $r = Invoke-WebRequest "$Base/upload?name=results-round4.zip" -Method Post -InFile $zip -ContentType 'application/zip' -UseBasicParsing
    Write-Host $r.Content -ForegroundColor Green
    Write-Host 'All done. You can close this window.' -ForegroundColor Green
} catch {
    Write-Host "Sending failed: $_" -ForegroundColor Red
    Write-Host "The results are in $zip : upload that file from the round page instead (button at the bottom)."
}
