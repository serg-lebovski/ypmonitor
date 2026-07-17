# Сборка трёх установщиков YPMon в виде одиночных исполняемых файлов:
#   1) dist/windows-server  — Сервер для Windows
#   2) dist/windows-agent   — Агент для Windows
#   3) dist/linux-server    — Сервер для Linux
#
# Использование:
#   pwsh build/build-all.ps1                 # собрать всё
#   pwsh build/build-all.ps1 -Target server-win
#
param(
    [ValidateSet('all','server-win','agent-win','server-linux')]
    [string]$Target = 'all'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'

$common = @(
    '-c', 'Release',
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false'
)

function Publish($proj, $rid, $outName, $installerDir) {
    $out = Join-Path $dist $outName
    Write-Host "==> Сборка $outName ($rid)..." -ForegroundColor Cyan
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    dotnet publish (Join-Path $root $proj) @common -r $rid -o $out
    if ($LASTEXITCODE -ne 0) { throw "Ошибка сборки $outName" }

    # Копируем скрипты установки рядом с бинарником.
    # ВАЖНО: не копируем исполняемые файлы/библиотеки из ассетов — иначе случайно оставленный там
    # старый Ypmon.Agent.exe затрёт только что собранный (был баг: во всех установщиках оказывалась
    # версия 1.0.0.0). Ассеты — это только скрипты установки и конфиги.
    $assets = Join-Path $root $installerDir
    if (Test-Path $assets) {
        Get-ChildItem $assets -Recurse -File |
            Where-Object { $_.Extension -notin '.exe', '.dll' } |
            ForEach-Object {
                $rel = $_.FullName.Substring($assets.Length).TrimStart('\','/')
                $dest = Join-Path $out $rel
                New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
                Copy-Item $_.FullName $dest -Force
            }
    }
    # Чистим лишнее
    Get-ChildItem $out -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force
    Remove-Item (Join-Path $out 'appsettings.Development.json') -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $out 'web.config') -Force -ErrorAction SilentlyContinue
    Write-Host "    Готово: $out" -ForegroundColor Green
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null

if ($Target -in 'all','server-win') {
    Publish 'src/Ypmon.Server/Ypmon.Server.csproj' 'win-x64' 'windows-server' 'installers/windows-server'
}
if ($Target -in 'all','agent-win') {
    Publish 'src/Ypmon.Agent/Ypmon.Agent.csproj' 'win-x64' 'windows-agent' 'installers/windows-agent'
}
if ($Target -in 'all','server-linux') {
    Publish 'src/Ypmon.Server/Ypmon.Server.csproj' 'linux-x64' 'linux-server' 'installers/linux-server'
}

Write-Host ""
Write-Host "Все установщики собраны в папке: $dist" -ForegroundColor Green
Write-Host " - windows-server : запустите install-service.ps1 от администратора"
Write-Host " - windows-agent  : запустите install-service.ps1 от администратора"
Write-Host " - linux-server   : sudo ./install.sh"
