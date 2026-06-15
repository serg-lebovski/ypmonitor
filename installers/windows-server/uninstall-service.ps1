# Удаление службы YPMon Server.
#Requires -RunAsAdministrator
$svc = 'YpmonServer'
if (Get-Service $svc -ErrorAction SilentlyContinue) {
    Stop-Service $svc -ErrorAction SilentlyContinue
    sc.exe delete $svc | Out-Null
    Write-Host "Служба '$svc' удалена." -ForegroundColor Green
} else {
    Write-Host "Служба '$svc' не найдена."
}
