$ErrorActionPreference = 'Stop'
$repo = "c:\data\dev\ypmon"

$srv = Join-Path $env:TEMP "ypsrv_e2e"
Remove-Item $srv -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $srv | Out-Null
Copy-Item (Join-Path $repo "dist\windows-server\*") $srv -Recurse
$env:Server__HttpPort = "8096"
$ps = Start-Process (Join-Path $srv "Ypmon.Server.exe") -PassThru -WorkingDirectory $srv `
    -RedirectStandardOutput (Join-Path $env:TEMP "e2e_srv.log") -RedirectStandardError (Join-Path $env:TEMP "e2e_srv.err")
Start-Sleep -Seconds 7
python (Join-Path $repo "scripts\seed_test.py") (Join-Path $srv "data\ypmon.db")

$ag = Join-Path $env:TEMP "ypag_e2e"
Remove-Item $ag -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $ag | Out-Null
Copy-Item (Join-Path $repo "dist\windows-agent\*") $ag -Recurse
$cfg = @{ serverUrl = "http://127.0.0.1:8096"; apiKey = "testkey123"; reportIntervalSeconds = 15; pgDumpPath = "pg_dump"; postgresJobs = @(); fileArchiveJobs = @(); mssql = @{ enabled = $false; logFolder = ""; filePattern = "*.txt" } } | ConvertTo-Json -Depth 6
Set-Content (Join-Path $ag "config.json") $cfg -Encoding UTF8
$pa = Start-Process (Join-Path $ag "Ypmon.Agent.exe") -ArgumentList "--console" -PassThru -WorkingDirectory $ag
Start-Sleep -Seconds 8

$snap = Get-Content (Join-Path $ag "snapshot.json") -Raw | ConvertFrom-Json
"АГЕНТ: accepted=$($snap.lastReportAccepted) client='$($snap.resolvedClientName)' server='$($snap.resolvedServerName)' msg='$($snap.lastReportMessage)'"

Stop-Process -Id $pa.Id -Force -ErrorAction SilentlyContinue
if (-not $ps.HasExited) { Stop-Process -Id $ps.Id -Force }
"--- server err ---"
Get-Content (Join-Path $env:TEMP "e2e_srv.err") -ErrorAction SilentlyContinue | Select-Object -First 5
