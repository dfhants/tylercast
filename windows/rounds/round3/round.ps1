# TylerCast Windows test round 3: the tray app itself. Run from PowerShell with the one line on the round page:
#   irm <page>/round.ps1 | iex
# Rounds 1-2 proved the engine (Intel encoder, smooth picture, AAC sound on the TV). This round is
# the real app: first launch, the flyout by the tray, casting from it, Fit to TV, settings, exit.
# It also checks round 2's fixes (AAC instead of AC-3, more slack for sound) in the app's log.
# PowerShell 5.1 compatible; plain ASCII.

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
$Base = '__BASE__'
$Work = Join-Path ([Environment]::GetFolderPath('Desktop')) 'TylerCast-round3'
$Results = Join-Path $Work 'results'
New-Item -ItemType Directory -Force -Path $Results | Out-Null
Set-Location $Work
Start-Transcript -Path (Join-Path $Results 'console.txt') -Force | Out-Null
$Answers = Join-Path $Results 'answers.txt'
"TylerCast round 3, $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" | Set-Content $Answers
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
# First launch as a new user would see it: no settings from earlier rounds.
if (Test-Path $Settings) { Remove-Item $Settings -Force }
$logFrom = if (Test-Path $Log) { (Get-Content $Log).Count } else { 0 }
$profiles = Get-NetConnectionProfile -ErrorAction SilentlyContinue
foreach ($p in $profiles) { Note 'network_profile' "$($p.InterfaceAlias): $($p.NetworkCategory)" }
# The flyout drawn on this laptop (its fonts and scaling), every state, light and dark.
& $exe --screenshot (Join-Path $Results 'screens') | Out-Null

# ------------------------------------------------------------------ first launch
Step 2 $total 'Start TylerCast for the first time'
Say @'
TylerCast will now start, as if you had just installed it. Watch the corner of the
screen where the clock is.
'@
Read-Host 'Press Enter to start TylerCast' | Out-Null
Start-Process $exe
Start-Sleep -Seconds 4
Ask 'Did a TylerCast panel open by itself, next to the clock?' 'first_panel_opened' | Out-Null
Ask 'Did a notification say where TylerCast is (in the tray, under ^)?' 'first_notification' | Out-Null
Say 'Click somewhere else on the screen: the panel should close. Then find the TylerCast icon (a small TV) next to the clock, or under ^, and click it.'
Ask 'Did the panel close when you clicked elsewhere, and open again from the icon?' 'panel_toggle' | Out-Null

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
Ask 'Is the laptop''s screen on the TV now?' 'cast_seen' | Out-Null
Ask 'Is the sound coming from the TV (and not the laptop)?' 'cast_sound' | Out-Null
Say 'Let it play for about 30 seconds while you watch.'
AskText 'How smooth is the moving picture? 1 = very jerky ... 5 = smooth' 'smoothness' | Out-Null
AskText 'Does the sound keep in step with the picture? (y / n / a few words)' 'lipsync' | Out-Null

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
Step 6 $total 'Disconnect, and how it looks'
Say 'Open the panel and click Disconnect.'
Ask 'Did the TV stop showing the laptop (back to its own picture)?' 'disconnect' | Out-Null
Ask 'Did the laptop''s own sound come back?' 'sound_back' | Out-Null
AskText 'How does the panel look next to Windows'' own Wi-Fi panel? 1 = out of place ... 5 = looks like part of Windows' 'looks' | Out-Null
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
$zip = Join-Path $Work 'results-round3.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $Results '*') -DestinationPath $zip -Force
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Sending $mb MB to the Mac..."
try {
    $r = Invoke-WebRequest "$Base/upload?name=results-round3.zip" -Method Post -InFile $zip -ContentType 'application/zip' -UseBasicParsing
    Write-Host $r.Content -ForegroundColor Green
    Write-Host 'All done. You can close this window.' -ForegroundColor Green
} catch {
    Write-Host "Sending failed: $_" -ForegroundColor Red
    Write-Host "The results are in $zip : upload that file from the round page instead (button at the bottom)."
}
