# Установка YPMon Server как службы Windows.
# Запускать от имени администратора в папке с Ypmon.Server.exe.
#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'
$svc = 'YpmonServer'
$exe = Join-Path $PSScriptRoot 'Ypmon.Server.exe'

if (-not (Test-Path $exe)) { throw "Не найден $exe" }

if (Get-Service $svc -ErrorAction SilentlyContinue) {
    Write-Host "Служба уже существует, останавливаю и удаляю..."
    Stop-Service $svc -ErrorAction SilentlyContinue
    sc.exe delete $svc | Out-Null
    Start-Sleep -Seconds 2
}

New-Service -Name $svc -BinaryPathName "`"$exe`"" -DisplayName 'YPMon Server' `
    -Description 'Сервер мониторинга резервного копирования YPMon' -StartupType Automatic | Out-Null

# Перезапуск при сбое.
sc.exe failure $svc reset= 60 actions= restart/5000/restart/5000/restart/5000 | Out-Null

Start-Service $svc
$port = 8080
try { $port = (Get-Content (Join-Path $PSScriptRoot 'appsettings.json') -Raw | ConvertFrom-Json).Server.HttpPort } catch {}
Write-Host ""
Write-Host "Служба '$svc' установлена и запущена." -ForegroundColor Green
Write-Host "Откройте веб-интерфейс:  http://<адрес-сервера>:$port/" -ForegroundColor Cyan
Write-Host "При первом входе создайте администратора."
