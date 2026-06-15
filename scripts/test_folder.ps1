$ErrorActionPreference = 'Stop'
$bin = "c:\data\dev\ypmon\src\Ypmon.Agent\bin\Debug\net8.0-windows"
$w = Join-Path $env:TEMP "ypfolder"
Remove-Item $w -Recurse -Force -EA SilentlyContinue
New-Item -ItemType Directory $w | Out-Null
Copy-Item "$bin\*" $w -Recurse

# Папка "бэкапов" внешней программы
$bk = Join-Path $w "aomei"; New-Item -ItemType Directory $bk | Out-Null
1..3 | ForEach-Object { Set-Content (Join-Path $bk "backup_$_.adi") ("x" * (1024 * $_)) }

$cfg = @{
  serverUrl=""; apiKey=""; reportIntervalSeconds=15; availabilityIntervalSeconds=10; pgDumpPath="";
  postgresJobs=@(); fileArchiveJobs=@();
  folderMonitorJobs=@(@{ name="AOMEI"; enabled=$true; backupFolder=$bk; filePattern="*.adi"; warnIfOlderThanHours=0; logsFolder=""; logsPattern="*.txt"; networkUsername=""; networkPassword="" });
  mssql=@{ enabled=$false; logFolder=""; filePattern="*.txt" }
} | ConvertTo-Json -Depth 6
Set-Content (Join-Path $w "config.json") $cfg -Encoding UTF8

$p = Start-Process (Join-Path $w "Ypmon.Agent.exe") -ArgumentList "--console" -PassThru -WorkingDirectory $w
Start-Sleep -Seconds 7
Stop-Process -Id $p.Id -Force -EA SilentlyContinue

$snap = Get-Content (Join-Path $w "snapshot.json") -Raw | ConvertFrom-Json
$j = $snap.lastReport.jobs | Where-Object { $_.name -eq "AOMEI" }
"Задание мониторинга: name=$($j.name) type=$($j.type) outcome=$($j.outcome) count=$($j.backupCount) size=$($j.totalSizeBytes)"
"Ожидается: type=3 (FolderMonitor), count=3"
