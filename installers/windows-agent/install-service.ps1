# Установка YPMon Agent как службы Windows.
# Запускать от имени администратора в папке с Ypmon.Agent.exe.
#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'
$svc = 'YpmonAgent'
$exe = Join-Path $PSScriptRoot 'Ypmon.Agent.exe'

if (-not (Test-Path $exe)) { throw "Не найден $exe" }

if (Get-Service $svc -ErrorAction SilentlyContinue) {
    Write-Host "Служба уже существует, останавливаю и удаляю..."
    Stop-Service $svc -ErrorAction SilentlyContinue
    sc.exe delete $svc | Out-Null
    Start-Sleep -Seconds 2
}

New-Service -Name $svc -BinaryPathName "`"$exe`"" -DisplayName 'YPMon Agent' `
    -Description 'Агент мониторинга и резервного копирования YPMon' -StartupType Automatic | Out-Null

sc.exe failure $svc reset= 60 actions= restart/5000/restart/5000/restart/5000 | Out-Null

Start-Service $svc
$port = 8088
try { $port = (Get-Content (Join-Path $PSScriptRoot 'config.json') -Raw | ConvertFrom-Json).localPort } catch {}
Write-Host ""
Write-Host "Служба '$svc' установлена и запущена." -ForegroundColor Green
Write-Host "Настройте агента в локальном веб-интерфейсе:  http://127.0.0.1:$port/" -ForegroundColor Cyan
Write-Host "Укажите адрес сервера YPMon и API-ключ, задайте задания бэкапа."
