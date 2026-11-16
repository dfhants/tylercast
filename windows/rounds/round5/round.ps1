# TylerCast Windows test round 5: the installer. Run from PowerShell with the one line on the round page:
#   irm <page>/round.ps1 | iex
# Rounds 3-4 proved the app. This round installs it as a user would (per user, no admin),
# starts it from the Start menu, casts briefly, and uninstalls it from Settings > Apps.
# PowerShell 5.1 compatible; plain ASCII.

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
$Base = '__BASE__'
$Setup = '__SETUP__'
# SHA-256 of each download, filled in by the Mac: nothing is run unless it matches.
$Hashes = @{ __HASHES__ }
$Work = Join-Path ([Environment]::GetFolderPath('Desktop')) 'TylerCast-round5'
$Results = Join-Path $Work 'results'
New-Item -ItemType Directory -Force -Path $Results | Out-Null
Set-Location $Work
Start-Transcript -Path (Join-Path $Results 'console.txt') -Force | Out-Null
$Answers = Join-Path $Results 'answers.txt'
"TylerCast round 5, $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" | Set-Content $Answers
$Data = Join-Path $env:LOCALAPPDATA 'TylerCast'
$Log = Join-Path $Data 'TylerCast.log'
$Dir = Join-Path $env:LOCALAPPDATA 'Programs\TylerCast'
$Lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\TylerCast.lnk'
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
$total = 5

# ------------------------------------------------------------------ download
Step 1 $total 'Download the installer'
Get-Process -Name 'TylerCast' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$installer = Join-Path $Work $Setup
try {
    Invoke-WebRequest "$Base/files/$Setup" -OutFile $installer -UseBasicParsing
    Write-Host "Downloaded $Setup, $([math]::Round((Get-Item $installer).Length / 1MB)) MB"
    $got = (Get-FileHash $installer -Algorithm SHA256).Hash
    if ($got -ne $Hashes[$Setup]) {
        Remove-Item $installer -Force
        throw "the installer doesn't match the one the Mac sent (SHA-256 $got): it was changed on the way"
    }
    Write-Host 'Installer checked: it is exactly the one the Mac sent.' -ForegroundColor Green
    Note 'installer_sha256' $got
} catch {
    Write-Host "Download failed: $_" -ForegroundColor Red
    Stop-Transcript | Out-Null
    return
}
$logFrom = if (Test-Path $Log) { (Get-Content $Log).Count } else { 0 }

# ------------------------------------------------------------------ install
Step 2 $total 'Install TylerCast'
Say @'
The installer opens now. Click through it as you would for any app:
 * Tick "Start TylerCast when I sign in to Windows" (we'll check it, and remove it at the end).
 * At the end, leave "Open TylerCast" ticked and click Finish.
'@
Read-Host 'Press Enter to open the installer' | Out-Null
$p = Start-Process $installer -PassThru -Wait
Note 'installer_exit' $p.ExitCode
Note 'installed_exe' (Test-Path (Join-Path $Dir 'TylerCast.exe'))
Note 'start_menu_entry' (Test-Path $Lnk)
Note 'run_at_sign_in' ((Get-ItemProperty -Path $RunKey -Name TylerCast -ErrorAction SilentlyContinue).TylerCast)
Ask 'Did the installer look right (TylerCast icon, no admin password asked)?' 'installer_looks' | Out-Null
Start-Sleep -Seconds 3
Ask 'Did TylerCast open after Finish (its panel by the clock)?' 'opened_after_install' | Out-Null
Ask 'Is the tray icon a small screen with a T on it?' 'tray_glyph' | Out-Null

# ------------------------------------------------------------------ start menu
Step 3 $total 'Start menu'
Say @'
Right-click the TylerCast icon and click Exit. Then press the Windows key, type TylerCast,
and open it from the Start menu.
'@
Ask 'Did the Start menu show TylerCast with its icon (blue tile, screen with a T)?' 'start_menu_icon' | Out-Null
Ask 'Did TylerCast open from there?' 'start_menu_opens' | Out-Null

# ------------------------------------------------------------------ cast
Step 4 $total 'A short cast'
Say @'
Turn the TV on. In the TylerCast panel, click the TV and Cast. (Windows may ask about the
network again for the installed copy: tick Private networks, click Allow.)
While it casts, look at the tray icon, then click Disconnect.
'@
Ask 'Did the TV show the laptop''s screen?' 'cast_seen' | Out-Null
Ask 'While casting, was the tray icon filled in your accent colour?' 'tray_casting' | Out-Null

# ------------------------------------------------------------------ uninstall
Step 5 $total 'Uninstall from Settings'
Say @'
Open Settings > Apps > Installed apps, find TylerCast (search for it), click the ... beside it,
and click Uninstall. Confirm.
'@
Ask 'Was TylerCast listed in Installed apps, with its icon?' 'settings_listed' | Out-Null
Read-Host 'Press Enter once the uninstall has finished' | Out-Null
Start-Sleep -Seconds 2
Note 'after_uninstall_exe' (Test-Path (Join-Path $Dir 'TylerCast.exe'))
Note 'after_uninstall_start_menu' (Test-Path $Lnk)
Note 'after_uninstall_run_at_sign_in' ($null -ne (Get-ItemProperty -Path $RunKey -Name TylerCast -ErrorAction SilentlyContinue))
Note 'after_uninstall_running' ($null -ne (Get-Process -Name TylerCast -ErrorAction SilentlyContinue))
AskText 'Anything that looked wrong, or felt awkward? (Enter to skip)' 'notes' | Out-Null

# Leave nothing behind.
Get-Process -Name 'TylerCast' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $RunKey -Name 'TylerCast' -ErrorAction SilentlyContinue

Stop-Transcript | Out-Null
if (Test-Path $Log) { Get-Content $Log | Select-Object -Skip $logFrom | Set-Content (Join-Path $Results 'TylerCast.log') }
$zip = Join-Path $Work 'results-round5.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $Results '*') -DestinationPath $zip -Force
try {
    $r = Invoke-WebRequest "$Base/upload?name=results-round5.zip" -Method Post -InFile $zip -ContentType 'application/zip' -UseBasicParsing
    Write-Host $r.Content -ForegroundColor Green
    Write-Host 'All done. You can close this window.' -ForegroundColor Green
} catch {
    Write-Host "Sending failed: $_" -ForegroundColor Red
    Write-Host "The results are in $zip : upload that file from the round page instead (button at the bottom)."
}
