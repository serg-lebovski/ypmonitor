$ErrorActionPreference = 'Stop'
$bin = "c:\data\dev\ypmon\src\Ypmon.Agent\bin\Debug\net8.0-windows"
$w = Join-Path $env:TEMP "ypgrid"
Remove-Item $w -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $w | Out-Null
Copy-Item (Join-Path $bin '*') $w -Recurse

$cfg = @{ serverUrl=""; apiKey=""; reportIntervalSeconds=60; availabilityIntervalSeconds=30; pgDumpPath="";
  postgresJobs=@(@{name="PG1";enabled=$true;host="localhost";port=5432;username="postgres";password="";database="db";backupDir="";retentionCount=7;intervalMinutes=1440;extraArgs="--format=custom"});
  fileArchiveJobs=@(@{name="Файлы1";enabled=$false;sourcePaths=@();archiveDir="";retentionCount=7;intervalMinutes=1440;networkUsername="";networkPassword=""});
  folderMonitorJobs=@(@{name="AOMEI1";enabled=$true;backupFolder="C:\tmp";filePattern="*.adi";warnIfOlderThanHours=26;logsFolder="";logsPattern="*.txt";networkUsername="";networkPassword=""});
  mssql=@{enabled=$false;logFolder="";filePattern="*.txt"} } | ConvertTo-Json -Depth 6
Set-Content (Join-Path $w "config.json") $cfg -Encoding UTF8

$p = Start-Process (Join-Path $w "Ypmon.Agent.exe") -PassThru -WorkingDirectory $w
Start-Sleep -Seconds 6
"Окно открыто (HasExited=false): $($p.HasExited)"
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; "закрыто" } else { "УПАЛО код $($p.ExitCode)" }
