$ErrorActionPreference = 'Stop'
$repo = "c:\data\dev\ypmon"
$srvDll = Join-Path $repo "src\Ypmon.Server\bin\Debug\net8.0\Ypmon.Server.dll"
$agentExe = Join-Path $repo "src\Ypmon.Agent\bin\Debug\net8.0-windows\Ypmon.Agent.exe"

$wd = Join-Path $env:TEMP "ypupd"
Remove-Item $wd -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $wd | Out-Null

$env:Server__HttpPort = "8095"
$ps = Start-Process dotnet -ArgumentList "`"$srvDll`"" -PassThru -WorkingDirectory $wd `
    -RedirectStandardOutput (Join-Path $env:TEMP "upd_srv.log") -RedirectStandardError (Join-Path $env:TEMP "upd_srv.err")
Start-Sleep -Seconds 7
python (Join-Path $repo "scripts\seed_test.py") (Join-Path $wd "data\ypmon.db") | Out-Null

# Кладём агент в agent-updates сервера
$upd = Join-Path $wd "agent-updates"
Copy-Item $agentExe (Join-Path $upd "Ypmon.Agent.exe") -Force

$h = @{ "X-Api-Key" = "testkey123" }
try { $v = Invoke-RestMethod "http://127.0.0.1:8095/api/agent/version" -Headers $h -TimeoutSec 5; "VERSION (с ключом): available=$($v.available) version=$($v.version)" } catch { "VERSION FAIL: $($_.Exception.Message)" }
try { Invoke-RestMethod "http://127.0.0.1:8095/api/agent/version" -TimeoutSec 5 | Out-Null; "БЕЗ КЛЮЧА: НЕ должно было пройти!" } catch { "БЕЗ КЛЮЧА (ожидаемо 401): $($_.Exception.Response.StatusCode)" }
try {
  Invoke-WebRequest "http://127.0.0.1:8095/api/agent/download" -Headers $h -OutFile (Join-Path $wd "dl.exe") -TimeoutSec 20
  "DOWNLOAD: $((Get-Item (Join-Path $wd 'dl.exe')).Length) байт"
} catch { "DOWNLOAD FAIL: $($_.Exception.Message)" }

if (-not $ps.HasExited) { Stop-Process -Id $ps.Id -Force }
