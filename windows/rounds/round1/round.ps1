# TylerCast Windows test round 1. Run from PowerShell with the one line on the round page:
#   irm <page>/round.ps1 | iex
# Everything goes in Desktop\TylerCast-round1; results are zipped and sent back to the Mac
# at the end. Nothing is installed and no settings are changed (the speakers' mute is
# put back after each check). PowerShell 5.1 compatible.

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'   # the progress bar makes downloads 10x slower in 5.1
$Base = '__BASE__'
$Work = Join-Path ([Environment]::GetFolderPath('Desktop')) 'TylerCast-round1'
$Results = Join-Path $Work 'results'
New-Item -ItemType Directory -Force -Path $Results | Out-Null
Set-Location $Work
Start-Transcript -Path (Join-Path $Results 'console.txt') -Force | Out-Null
$Answers = Join-Path $Results 'answers.txt'
"TylerCast round 1, $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" | Set-Content $Answers

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

$exe = Join-Path $Work 'tylercast.exe'

# ------------------------------------------------------------------ download
Step 1 6 'Download TylerCast (about 50 MB)'
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
Step 2 6 'Self-test: no TV needed (about 1-2 minutes)'
Say @'
TylerCast now checks everything it can on its own: the encoders, two ways of
capturing the screen, the sound, and the network. Please don't touch the
mouse or keyboard until it finishes.

 * A pink box appears at the top-left of the screen twice: that's the screen check.
 * You'll hear two short beeps (2-3 seconds each): that's the sound check.
   If your volume is very low, turn it up a little now so the check can hear them.
'@
Read-Host 'Press Enter to start the self-test' | Out-Null
& $exe selftest (Join-Path $Results 'selftest')
Note 'selftest_exit' $LASTEXITCODE

# ------------------------------------------------------------------ TV checks
Say @'
Now the TV. Please:
 * switch the TV on (not standby), on its normal TV or HDMI input, and
 * sit where you can see it and this laptop.

The first time TylerCast streams, Windows may ask whether to allow it on the
network ("Windows Defender Firewall has blocked some features"). Tick
"Private networks" and click "Allow". Without that the TV can't fetch the picture.
'@
Read-Host 'Press Enter when the TV is on' | Out-Null

Step 3 6 'Colour bars on the TV (30 seconds)'
Say 'TylerCast sends a pre-made test picture (colour bars with a moving white box). Watch the TV.'
& $exe play --test-pattern --seconds 30
Note 'pattern_exit' $LASTEXITCODE
$bars = Ask 'Did the TV show colour bars with a white box moving across?' 'pattern_seen'
if ($bars -eq 'n') {
    AskText 'What did the TV show or say instead? (a few words, or Enter to skip)' 'pattern_tv_said' | Out-Null
}

Step 4 6 'This screen on the TV, with sound (45 seconds)'
Say @'
Before you press Enter: start something with sound on this laptop, for example
a YouTube video or music in the browser, and leave it playing.

TylerCast will show this screen on the TV with the sound, and mute the laptop's
own speakers while it runs (they come back afterwards). Watch and listen to the TV.
'@
Read-Host 'Press Enter when something is playing' | Out-Null
& $exe play --seconds 45
Note 'screen_exit' $LASTEXITCODE
$pic = Ask 'Did the TV show this laptop''s screen?' 'screen_seen'
$snd = Ask 'Did the sound come out of the TV?' 'screen_tv_sound'
Ask 'Were the laptop''s own speakers silent while it ran?' 'screen_laptop_silent' | Out-Null
AskText 'How smooth was it? 1 = very jerky ... 5 = smooth. (Enter a number)' 'screen_smoothness' | Out-Null
AskText 'Were the edges cut off, or is there a black border? (a few words, or Enter to skip)' 'screen_edges' | Out-Null

Step 5 6 'Second try with different settings (45 seconds)'
if ($pic -ne 'y') {
    Say 'Trying the other screen-capture method, with AAC sound and the laptop speakers on. Keep something playing.'
    Read-Host 'Press Enter to start' | Out-Null
    & $exe play --seconds 45 --capture gdi --audio aac --pc-sound
    Note 'retry' 'gdi+aac+pc-sound'
} elseif ($snd -ne 'y') {
    Say 'The picture worked; now trying AAC sound instead of AC-3, with the laptop speakers on as well. Keep something playing.'
    Read-Host 'Press Enter to start' | Out-Null
    & $exe play --seconds 45 --audio aac --pc-sound
    Note 'retry' 'aac+pc-sound'
} else {
    Say 'Picture and sound both worked. This run tries AAC sound with the laptop speakers left on, to compare. Keep something playing.'
    Read-Host 'Press Enter to start' | Out-Null
    & $exe play --seconds 45 --audio aac --pc-sound
    Note 'retry' 'aac+pc-sound (comparison)'
}
Note 'retry_exit' $LASTEXITCODE
Ask 'Did the TV show this laptop''s screen this time?' 'retry_seen' | Out-Null
Ask 'Did the sound come out of the TV this time?' 'retry_tv_sound' | Out-Null
AskText 'Anything else you noticed? (Enter to skip)' 'notes' | Out-Null

# ------------------------------------------------------------------ send back
Step 6 6 'Send the results to the Mac'
Stop-Transcript | Out-Null
$log = Join-Path $env:LOCALAPPDATA 'TylerCast\TylerCast.log'
if (Test-Path $log) { Copy-Item $log (Join-Path $Results 'TylerCast.log') -Force }
$zip = Join-Path $Work 'results-round1.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $Results '*') -DestinationPath $zip -Force
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Sending $mb MB to the Mac..."
try {
    $r = Invoke-WebRequest "$Base/upload?name=results-round1.zip" -Method Post -InFile $zip -ContentType 'application/zip' -UseBasicParsing
    Write-Host $r.Content -ForegroundColor Green
    Write-Host 'All done. You can close this window.' -ForegroundColor Green
} catch {
    Write-Host "Sending failed: $_" -ForegroundColor Red
    Write-Host "The results are in $zip : upload that file from the round page instead (button at the bottom)."
}
