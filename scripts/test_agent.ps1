# Функциональный тест агента: архивация файлов + retention + снапшот статуса.
$ErrorActionPreference = 'Stop'
$bin = "c:\data\dev\ypmon\src\Ypmon.Agent\bin\Debug\net8.0-windows"
$w = Join-Path $env:TEMP "ypagfunc"
Remove-Item $w -Recurse -Force -EA SilentlyContinue
New-Item -ItemType Directory $w | Out-Null
Copy-Item "$bin\*" $w -Recurse

$src = Join-Path $w "src"; New-Item -ItemType Directory $src | Out-Null
Set-Content (Join-Path $src "data.txt") "hello backup"
$arc = Join-Path $w "arc"; New-Item -ItemType Directory $arc | Out-Null

# 5 старых dummy-архивов (должны попасть под retention)
for ($i=1; $i -le 5; $i++) {
  $f = Join-Path $arc "T_old$i.zip"
  Set-Content $f "x"
  (Get-Item $f).CreationTime = (Get-Date).AddDays(-$i)
}

# config.json: одно задание архивации, retention=3, интервал 0 (запуск только по флагу)
$cfg = @{
  serverUrl=""; apiKey=""; reportIntervalSeconds=15; pgDumpPath="pg_dump";
  postgresJobs=@();
  fileArchiveJobs=@(@{ name="T"; enabled=$true; sourcePaths=@($src); archiveDir=$arc; retentionCount=3; intervalMinutes=0 });
  mssql=@{ enabled=$false; logFolder=""; filePattern="*.txt" }
} | ConvertTo-Json -Depth 6
Set-Content (Join-Path $w "config.json") $cfg -Encoding UTF8

# Сигнал «выполнить сейчас»
Set-Content (Join-Path $w "runnow.flag") "go"

"Архивов до запуска: $((Get-ChildItem $arc -Filter 'T_*.zip').Count)"
$p = Start-Process (Join-Path $w "Ypmon.Agent.exe") -ArgumentList "--console" -PassThru -WorkingDirectory $w
Start-Sleep -Seconds 10
Stop-Process -Id $p.Id -Force -EA SilentlyContinue

$zips = Get-ChildItem $arc -Filter 'T_*.zip' | Sort-Object CreationTime
"Архивов после запуска: $($zips.Count)  (ожидается 3 из-за retention)"
"Самый свежий: $($zips[-1].Name)  размер $($zips[-1].Length) Б"
"snapshot.json: $(Test-Path (Join-Path $w 'snapshot.json'))"
"state.json:    $(Test-Path (Join-Path $w 'state.json'))"
$snap = Get-Content (Join-Path $w "snapshot.json") -Raw | ConvertFrom-Json
"Снапшот: задание '$($snap.lastReport.jobs[0].name)' outcome=$($snap.lastReport.jobs[0].outcome) backupCount=$($snap.lastReport.jobs[0].backupCount)"
