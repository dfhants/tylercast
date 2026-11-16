# Checks the round page's one-liner in Windows PowerShell 5.1 (what testers use): it must run a
# script that arrives intact, and refuse one changed on the way. CI runs it against a dummy round.
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:RUNNER_TEMP 'round-test'
$res = Join-Path $env:RUNNER_TEMP 'round-test-results'
New-Item -ItemType Directory -Force -Path $dir, $res | Out-Null
Copy-Item rounds/round5/index.html $dir
Set-Content -Path (Join-Path $dir 'round.ps1') -Value "# test`n`$Hashes = @{ __HASHES__ }`nWrite-Output ('ROUND-SCRIPT-RAN ' + `$Hashes['x.bin'])" -NoNewline
[IO.File]::WriteAllBytes((Join-Path $dir 'x.bin'), [byte[]](1..200))
$log = Join-Path $env:RUNNER_TEMP 'round-server.log'
$server = Start-Process python -ArgumentList 'tools/round-server.py', $dir, $res, '--port', '8799' -PassThru -RedirectStandardOutput $log -NoNewWindow
Start-Sleep -Seconds 3
$base = ((Get-Content $log) -match 'ROUND PAGE' | Select-Object -First 1) -replace '^ROUND PAGE ', '' -replace '/$', ''
Write-Host "base $base"
$page = (Invoke-WebRequest "$base/" -UseBasicParsing).Content
$line = [regex]::Match($page, '<code id="cmd">(.*?)</code>').Groups[1].Value
$line = [System.Net.WebUtility]::HtmlDecode($line)
Write-Host "one-liner: $line"
$out = & powershell.exe -NoProfile -Command $line 2>&1 | Out-String
Write-Host $out
$ok = $out -match 'ROUND-SCRIPT-RAN'
# The same line with a different expected hash must refuse to run.
$bad = $line -replace "-eq '[0-9A-F]{64}'", "-eq '$('0' * 64)'"
$out2 = & powershell.exe -NoProfile -Command $bad 2>&1 | Out-String
$refused = ($out2 -match 'changed on the way') -and ($out2 -notmatch 'ROUND-SCRIPT-RAN')
Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
Write-Host "runs intact script: $ok; refuses changed script: $refused"
if (-not ($ok -and $refused)) { exit 1 }
