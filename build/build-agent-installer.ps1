# Сборка установщика агента (Inno Setup).
# Сначала публикует single-file exe агента, затем компилирует YpmonAgent-Setup.exe.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# 1) Опубликовать агент в dist/windows-agent
pwsh -NoProfile -File (Join-Path $PSScriptRoot 'build-all.ps1') -Target agent-win
if ($LASTEXITCODE -ne 0) { throw "Ошибка публикации агента" }

# 2) Версия из csproj
$csproj = Join-Path $root 'src/Ypmon.Agent/Ypmon.Agent.csproj'
$ver = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $ver) { $ver = '1.0.0' }

# 3) Найти ISCC
$iscc = @(
  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
  "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
  "C:\Program Files\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "ISCC.exe (Inno Setup) не найден. Установите Inno Setup." }

$dist = Join-Path $root 'dist'
$distAgent = Join-Path $dist 'windows-agent'
$iss = Join-Path $root 'installers/agent/agent.iss'

Write-Host "==> Компиляция установщика версии $ver ..." -ForegroundColor Cyan
& $iscc "/DAppVer=$ver" "/DDistDir=$distAgent" "/DOutDir=$dist" $iss
if ($LASTEXITCODE -ne 0) { throw "Ошибка компиляции установщика" }

$setup = Join-Path $dist 'YpmonAgent-Setup.exe'
Write-Host ""
Write-Host "Готово: $setup" -ForegroundColor Green
"$ver" | Set-Content (Join-Path $dist 'agent-version.txt') -NoNewline
Write-Host "Версия: $ver (записана в dist/agent-version.txt)"
