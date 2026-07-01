# Выпуск обновления агента: собрать установщик и выложить его на сервер.
#
# Что делает:
#   1) Собирает YpmonAgent-Setup.exe (build-agent-installer.ps1).
#   2) Копирует установщик и version.txt в папку обновлений сервера (docker-том ypmon-updates),
#      откуда агенты его берут кнопкой «Проверить обновления».
#
# Использование:
#   $env:YPMON_SSH_PW = 'пароль'        # или скрипт спросит интерактивно
#   pwsh -File build/release-agent.ps1
#
# Параметры по умолчанию — прод-сервер. Секреты в скрипт НЕ зашиты.
param(
  [string]$ServerHost = '10.10.20.25',
  [string]$User       = 'admin_yp',
  [string]$Container  = 'ypmon-server',
  [string]$RemoteDir  = '/app/agent-updates',
  [string]$HostKey    = 'SHA256:OxWCkk5lCpimPDtDCQ7h8eKMlbGVYJd3aQudMSuFUuE'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# --- 1) Собрать установщик ---
Write-Host "==> Сборка установщика агента..." -ForegroundColor Cyan
pwsh -NoProfile -File (Join-Path $PSScriptRoot 'build-agent-installer.ps1')
if ($LASTEXITCODE -ne 0) { throw "Ошибка сборки установщика" }

$setup = Join-Path $root 'dist/YpmonAgent-Setup.exe'
$ver   = (Get-Content (Join-Path $root 'dist/agent-version.txt') -Raw).Trim()
if (-not (Test-Path $setup)) { throw "Не найден $setup" }
Write-Host "Версия: $ver" -ForegroundColor Green

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

# --- 4) Загрузка на сервер ---
Write-Host "==> Загрузка на $ServerHost ..." -ForegroundColor Cyan
& $pscp -batch -hostkey $HostKey -pw $pw $setup "$User@${ServerHost}:/tmp/YpmonAgent-Setup.exe"
if ($LASTEXITCODE -ne 0) { throw "Ошибка pscp" }

# --- 5) Внутрь контейнера + version.txt ---
$remote = @"
echo '$pw' | sudo -S docker cp /tmp/YpmonAgent-Setup.exe ${Container}:${RemoteDir}/YpmonAgent-Setup.exe
echo '$pw' | sudo -S docker exec ${Container} sh -c 'printf %s "$ver" > ${RemoteDir}/version.txt'
echo '$pw' | sudo -S rm -f /tmp/YpmonAgent-Setup.exe
echo '$pw' | sudo -S docker exec ${Container} sh -c 'ls -l ${RemoteDir}; echo VER=; cat ${RemoteDir}/version.txt'
"@
$remote | & $plink -batch -ssh "$User@$ServerHost" -pw $pw -hostkey $HostKey
if ($LASTEXITCODE -ne 0) { throw "Ошибка размещения на сервере" }

Write-Host ""
Write-Host "Готово: агент $ver выложен на $ServerHost ($RemoteDir)." -ForegroundColor Green
Write-Host "Агенты обновятся кнопкой «Проверить обновления»." -ForegroundColor Green
