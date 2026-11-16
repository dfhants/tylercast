# Installs TylerCast silently (with "start when I sign in"), checks what landed, uninstalls,
# and checks it all went. CI runs it on the installer it has just built. Exits 1 on any miss.
param([Parameter(Mandatory)] [string] $Setup)
$ErrorActionPreference = 'Stop'
$fails = 0
function Check($ok, $what) {
    if ($ok) { Write-Host "OK   $what" } else { Write-Host "FAIL $what"; $script:fails++ }
}
$dir = Join-Path $env:LOCALAPPDATA 'Programs\TylerCast'
$exe = Join-Path $dir 'TylerCast.exe'
$lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\TylerCast.lnk'
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstallKeys = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'

$p = Start-Process $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=startup', '/LOG=install.log' -Wait -PassThru
Check ($p.ExitCode -eq 0) "installer exit code $($p.ExitCode)"
Check (Test-Path $exe) "TylerCast.exe in $dir"
Check (Test-Path $lnk) 'Start menu entry'
$value = (Get-ItemProperty -Path $run -Name TylerCast -ErrorAction SilentlyContinue).TylerCast
Check ($value -eq "`"$exe`" --background") "start at sign-in: $value"
$entry = Get-ItemProperty $uninstallKeys -ErrorAction SilentlyContinue | Where-Object DisplayName -eq 'TylerCast'
Check ($null -ne $entry) "Settings > Apps entry: $($entry.DisplayName) $($entry.DisplayVersion)"
Check ((Get-Item $exe).VersionInfo.ProductVersion -like '0.2.0*') "version $((Get-Item $exe).VersionInfo.ProductVersion)"

# The installed app starts and stays up (it opens its flyout; the runner has a desktop).
$app = Start-Process $exe -PassThru
Start-Sleep -Seconds 8
Check (-not $app.HasExited) 'installed TylerCast runs'

$uninstaller = Join-Path $dir 'unins000.exe'
$p = Start-Process $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
Start-Sleep -Seconds 3 # the uninstaller hands over to a copy of itself, which finishes after
Check ($p.ExitCode -eq 0) "uninstaller exit code $($p.ExitCode)"
Check (-not (Get-Process -Name TylerCast -ErrorAction SilentlyContinue)) 'TylerCast stopped by the uninstaller'
Check (-not (Test-Path $exe)) 'TylerCast.exe removed'
Check (-not (Test-Path $lnk)) 'Start menu entry removed'
Check ($null -eq (Get-ItemProperty -Path $run -Name TylerCast -ErrorAction SilentlyContinue)) 'start at sign-in removed'
Check ($null -eq (Get-ItemProperty $uninstallKeys -ErrorAction SilentlyContinue | Where-Object DisplayName -eq 'TylerCast')) 'Settings > Apps entry removed'
Write-Host "$fails failed"
exit [int]($fails -gt 0)
