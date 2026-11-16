# TylerCast Windows test round 2. Run from PowerShell with the one line on the round page:
#   irm <page>/round.ps1 | iex
# Round 1 found: Intel's encoder failing (now fixed, we hope), Windows' AC-3 encoder
# crashing the process (now set up the way it needs, and isolated in the self-test),
# and only ~15 frames a second on battery (capture, convert and encode now run in
# parallel). This round checks all three. PowerShell 5.1 compatible.

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
$Base = '__BASE__'
$Work = Join-Path ([Environment]::GetFolderPath('Desktop')) 'TylerCast-round2'
$Results = Join-Path $Work 'results'
New-Item -ItemType Directory -Force -Path $Results | Out-Null
Set-Location $Work
Start-Transcript -Path (Join-Path $Results 'console.txt') -Force | Out-Null
$Answers = Join-Path $Results 'answers.txt'
"TylerCast round 2, $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" | Set-Content $Answers
$Log = Join-Path $env:LOCALAPPDATA 'TylerCast\TylerCast.log'

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
function LogLines() { if (Test-Path $Log) { return (Get-Content $Log).Count } else { return 0 } }
# The encoder TylerCast chose in the log lines written since $from.
function ChosenEncoder($from) {
    if (-not (Test-Path $Log)) { return '' }
    $new = Get-Content $Log | Select-Object -Skip $from
    $ok = $new | Where-Object { $_ -match 'Encoder trial: (.+?): OK' } | Select-Object -Last 1
    if ($ok -match 'Encoder trial: (.+?): OK') { return $Matches[1] }
    return ''
}

$exe = Join-Path $Work 'tylercast.exe'

# ------------------------------------------------------------------ power, download
Step 1 6 'Power and download'
$power = (Get-CimInstance -ClassName Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1)
if ($power -and $power.BatteryStatus -eq 1) {
    Say 'The laptop is on battery. Please plug it in: on battery Windows slows it down, which spoils the smoothness checks.'
    Read-Host 'Press Enter once it is plugged in (or to carry on anyway)' | Out-Null
}
$power = (Get-CimInstance -ClassName Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1)
if ($power) { Note 'battery_status' "$($power.BatteryStatus) (1 = on battery, 2 = plugged in), charge $($power.EstimatedChargeRemaining)%" }
try {
    Invoke-WebRequest "$Base/files/tylercast.exe" -OutFile $exe -UseBasicParsing
    Write-Host "Downloaded $([math]::Round((Get-Item $exe).Length / 1MB)) MB"
} catch {
    Write-Host "Download failed: $_" -ForegroundColor Red
    Write-Host 'Is the Mac still serving the round page? Try opening the page again.'
    Stop-Transcript | Out-Null
    return
}

# ------------------------------------------------------------------ selftest
Step 2 6 'Self-test: no TV needed (about 2-3 minutes)'
Say @'
TylerCast checks everything it can on its own. Please don't touch the mouse or
keyboard until it finishes.
 * A pink box appears at the top-left of the screen twice: the screen check.
 * You'll hear two short beeps: the sound check.
'@
Read-Host 'Press Enter to start the self-test' | Out-Null
& $exe selftest (Join-Path $Results 'selftest')
Note 'selftest_exit' $LASTEXITCODE

# ------------------------------------------------------------------ network
$profiles = Get-NetConnectionProfile -ErrorAction SilentlyContinue
foreach ($p in $profiles) { Note 'network_profile' "$($p.InterfaceAlias): $($p.NetworkCategory)" }
if ($profiles | Where-Object { $_.NetworkCategory -eq 'Public' }) {
    Say @'
This laptop's Wi-Fi is set to "Public network" again, which stops the TV connecting.
  Settings > Network & internet > Wi-Fi > (your network's properties) > Private network
'@
    Read-Host 'Press Enter once it says Private network (or to carry on anyway)' | Out-Null
}
Say 'If Windows asks whether TylerCast can use the network: tick "Private networks" and click "Allow".'
& $exe serve --test-pattern --seconds 6 | Out-Null

# ------------------------------------------------------------------ TV
Say @'
Now the TV: switch it on (normal TV or HDMI input, not standby), and sit where
you can see it and this laptop.

Then start something with sound on this laptop (a YouTube video with movement
is ideal: it shows how smooth the picture is) and leave it playing for the rest.
'@
Read-Host 'Press Enter when the TV is on and something is playing' | Out-Null

Step 3 6 'This screen on the TV with sound (45 seconds)'
Say 'The laptop''s own speakers go quiet while this runs (they come back after). Watch and listen to the TV.'
$from = LogLines
& $exe play --seconds 45
Note 'main_exit' $LASTEXITCODE
$enc = ChosenEncoder $from
Note 'main_encoder' $enc
Ask 'Did the TV show this laptop''s screen?' 'main_seen' | Out-Null
Ask 'Did the sound come out of the TV?' 'main_tv_sound' | Out-Null
Ask 'Were the laptop''s own speakers silent while it ran?' 'main_laptop_silent' | Out-Null
AskText 'How smooth was the moving picture? 1 = very jerky ... 5 = smooth (enter a number)' 'main_smoothness' | Out-Null
AskText 'Did the sound keep in step with the picture? (y / n / a few words)' 'main_lipsync' | Out-Null
AskText 'Were the edges of the screen cut off on the TV? (y / n / a few words)' 'main_edges' | Out-Null

Step 4 6 'Same again with AC-3 sound, the format the TV lists (45 seconds)'
Say 'Keep the video playing.'
& $exe play --seconds 45 --audio ac3
Note 'ac3_exit' $LASTEXITCODE
if ($LASTEXITCODE -ne 0) { Write-Host "(TylerCast stopped with code $LASTEXITCODE; that's noted for the Mac.)" -ForegroundColor Yellow }
Ask 'Did the TV show the screen this time?' 'ac3_seen' | Out-Null
Ask 'Did the sound come out of the TV?' 'ac3_tv_sound' | Out-Null

Step 5 6 'A comparison run (45 seconds)'
if ($enc -match 'Intel') {
    Say 'The main run used Intel''s hardware encoder. This one uses Windows'' software encoder, to compare smoothness. Keep the video playing.'
    & $exe play --seconds 45 --encoder 'H264 Encoder MFT'
    Note 'compare' 'software encoder'
} else {
    Say 'This one uses the other way of capturing the screen, to compare smoothness. Keep the video playing.'
    & $exe play --seconds 45 --capture gdi
    Note 'compare' 'gdi capture'
}
Note 'compare_exit' $LASTEXITCODE
AskText 'How smooth was it compared with step 3? (smoother / same / jerkier)' 'compare_smoothness' | Out-Null
AskText 'Anything else you noticed? (Enter to skip)' 'notes' | Out-Null

# ------------------------------------------------------------------ send back
Step 6 6 'Send the results to the Mac'
Stop-Transcript | Out-Null
if (Test-Path $Log) { Copy-Item $Log (Join-Path $Results 'TylerCast.log') -Force }
$zip = Join-Path $Work 'results-round2.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $Results '*') -DestinationPath $zip -Force
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Sending $mb MB to the Mac..."
try {
    $r = Invoke-WebRequest "$Base/upload?name=results-round2.zip" -Method Post -InFile $zip -ContentType 'application/zip' -UseBasicParsing
    Write-Host $r.Content -ForegroundColor Green
    Write-Host 'All done. You can close this window.' -ForegroundColor Green
} catch {
    Write-Host "Sending failed: $_" -ForegroundColor Red
    Write-Host "The results are in $zip : upload that file from the round page instead (button at the bottom)."
}
