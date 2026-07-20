# Выпуск обновления агента: собрать установщик и выложить его на сервер.
#
# Что делает:
#   1) Собирает YpmonAgent-Setup.exe (build-agent-installer.ps1).
#   2) Копирует установщик и version.txt в папку обновлений сервера (docker-том ypmon-updates),
#      откуда агенты его берут кнопкой «Проверить обновления».
#   3) Проверяет результат: сверяет SHA256 файла на сервере с локальным и читает version.txt.
#
# Использование:
#   $env:YPMON_SSH_PW = 'пароль'        # или скрипт спросит интерактивно
#   pwsh -File build/release-agent.ps1
#
#   -SkipBuild — выложить уже собранный dist/YpmonAgent-Setup.exe, не пересобирая.
#
# Параметры по умолчанию — прод-сервер. Секреты в скрипт НЕ зашиты.
param(
  [string]$ServerHost = '10.10.20.25',
  [string]$User       = 'admin_yp',
  [string]$Container  = 'ypmon-server',
  [string]$RemoteDir  = '/app/agent-updates',
  [string]$HostKey    = 'SHA256:OxWCkk5lCpimPDtDCQ7h8eKMlbGVYJd3aQudMSuFUuE',
  [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# --- 1) Собрать установщик ---
if ($SkipBuild) {
  Write-Host "==> Сборка пропущена (-SkipBuild)" -ForegroundColor Yellow
} else {
  Write-Host "==> Сборка установщика агента..." -ForegroundColor Cyan
  pwsh -NoProfile -File (Join-Path $PSScriptRoot 'build-agent-installer.ps1')
  if ($LASTEXITCODE -ne 0) { throw "Ошибка сборки установщика" }
}

$setup = Join-Path $root 'dist/YpmonAgent-Setup.exe'
$ver   = (Get-Content (Join-Path $root 'dist/agent-version.txt') -Raw).Trim()
if (-not (Test-Path $setup)) { throw "Не найден $setup" }
$localHash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
Write-Host "Версия: $ver" -ForegroundColor Green
Write-Host "SHA256: $localHash" -ForegroundColor DarkGray

# --- 2) Пароль SSH ---
$pw = $env:YPMON_SSH_PW
if (-not $pw) {
  $sec = Read-Host "Пароль SSH для $User@$ServerHost" -AsSecureString
  $pw  = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
           [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
}

# --- 3) Найти plink/pscp (PuTTY) ---
$putty = @("C:\Program Files\PuTTY", "C:\Program Files (x86)\PuTTY") |
         Where-Object { Test-Path (Join-Path $_ 'plink.exe') } | Select-Object -First 1
if (-not $putty) { throw "PuTTY (plink/pscp) не найден. Установите PuTTY." }
$plink = Join-Path $putty 'plink.exe'
$pscp  = Join-Path $putty 'pscp.exe'

<#
.SYNOPSIS
Выполняет команду на сервере через plink и возвращает её вывод.

Команда передаётся АРГУМЕНТОМ, а не через stdin. Раньше скрипт подавал многострочный
текст в stdin plink — тот открывал интерактивную сессию и ждал ввода, из-за чего выкладка
регулярно зависала («Software caused connection abort») уже после копирования файла.
Дополнительно: -T (без псевдотерминала) и жёсткий таймаут с принудительным завершением,
чтобы подвисший plink не оставался в памяти.
#>
function Invoke-Remote {
  param([Parameter(Mandatory)][string]$Command, [int]$TimeoutSec = 180, [string]$What = 'команда')

  $outFile = [IO.Path]::GetTempFileName()
  $errFile = [IO.Path]::GetTempFileName()
  try {
    $args = @('-batch', '-T', '-ssh', "$User@$ServerHost", '-pw', $pw, '-hostkey', $HostKey, $Command)
    $p = Start-Process -FilePath $plink -ArgumentList $args -NoNewWindow -PassThru `
                       -RedirectStandardOutput $outFile -RedirectStandardError $errFile

    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
      try { $p.Kill() } catch { }
      throw "$What : plink не ответил за $TimeoutSec с — процесс снят. Проверьте доступность $ServerHost."
    }

    $out = (Get-Content $outFile -Raw -ErrorAction SilentlyContinue)
    $err = (Get-Content $errFile -Raw -ErrorAction SilentlyContinue)
    if ($p.ExitCode -ne 0) {
      throw "$What : plink вернул код $($p.ExitCode).`n$err`n$out"
    }
    return $out
  }
  finally {
    Remove-Item $outFile, $errFile -Force -ErrorAction SilentlyContinue
  }
}

# --- 4) Копирование установщика на сервер ---
Write-Host "==> Загрузка на $ServerHost ..." -ForegroundColor Cyan
& $pscp -batch -hostkey $HostKey -pw $pw $setup "$User@${ServerHost}:/tmp/YpmonAgent-Setup.exe"
if ($LASTEXITCODE -ne 0) { throw "Ошибка pscp: установщик не скопирован на сервер" }

# --- 5) Внутрь контейнера + version.txt + проверка ---
# Команды идут отдельным shell-скриптом, а не строкой в аргументе plink: при передаче
# аргументом PowerShell переписывает вложенные кавычки, и перенаправление «>» срабатывало
# на хосте вместо контейнера (version.txt не обновлялся, выкладка оставалась незавершённой).
# Со скриптом-файлом вложенных кавычек нет вообще.
#
# version.txt пишется только если docker cp удался (&&) — иначе агенты увидели бы новый
# номер версии при старом файле установщика.
Write-Host "==> Размещение в контейнере $Container ..." -ForegroundColor Cyan

$remoteScript = @"
#!/bin/sh
set -e
echo '$pw' | sudo -S -p '' docker cp /tmp/YpmonAgent-Setup.exe ${Container}:${RemoteDir}/YpmonAgent-Setup.exe
sudo -n docker exec ${Container} sh -c 'printf %s $ver > ${RemoteDir}/version.txt'
sudo -n rm -f /tmp/YpmonAgent-Setup.exe
echo '---VERIFY---'
sudo -n docker exec ${Container} cat ${RemoteDir}/version.txt
echo
sudo -n docker exec ${Container} sha256sum ${RemoteDir}/YpmonAgent-Setup.exe
"@

$tmpScript = Join-Path ([IO.Path]::GetTempPath()) 'ypmon-deploy.sh'
# Только LF: скрипт исполняется на Linux, CRLF ломает шебанг и команды.
[IO.File]::WriteAllText($tmpScript, ($remoteScript -replace "`r`n", "`n"))
try {
  & $pscp -batch -hostkey $HostKey -pw $pw $tmpScript "$User@${ServerHost}:/tmp/ypmon-deploy.sh"
  if ($LASTEXITCODE -ne 0) { throw "Не удалось скопировать скрипт размещения на сервер" }
} finally {
  Remove-Item $tmpScript -Force -ErrorAction SilentlyContinue
}

# Скрипт удаляем сразу после выполнения — в нём пароль sudo.
$check = Invoke-Remote -What 'Размещение на сервере' `
                       -Command 'sh /tmp/ypmon-deploy.sh; rc=$?; rm -f /tmp/ypmon-deploy.sh; exit $rc'

# --- 6) Проверка результата ---
Write-Host "==> Проверка ..." -ForegroundColor Cyan
$verifyPart = ($check -split '---VERIFY---')[-1]
$lines      = ($verifyPart -split "`n") | ForEach-Object { $_.Trim() } | Where-Object { $_ }
$remoteVer  = $lines | Select-Object -First 1
$remoteHash = ($lines | Where-Object { $_ -match '^[0-9a-f]{64}\s' } | Select-Object -First 1) -replace '\s.*$', ''

if ($remoteVer -ne $ver) {
  throw "version.txt на сервере = '$remoteVer', ожидалось '$ver'. Выкладка НЕ завершена."
}
if ($remoteHash -ne $localHash) {
  throw "SHA256 на сервере ($remoteHash) не совпал с локальным ($localHash). Файл повреждён при передаче."
}

Write-Host ""
Write-Host "Готово: агент $ver выложен на $ServerHost ($RemoteDir)." -ForegroundColor Green
Write-Host "version.txt и SHA256 проверены — файл на сервере совпадает с локальной сборкой." -ForegroundColor Green
Write-Host "Агенты обновятся сами (или кнопкой «Проверить обновления»)." -ForegroundColor Green
