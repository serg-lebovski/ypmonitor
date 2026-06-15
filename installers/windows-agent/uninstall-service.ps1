# Удаление службы YPMon Agent.
#Requires -RunAsAdministrator
$svc = 'YpmonAgent'
if (Get-Service $svc -ErrorAction SilentlyContinue) {
    Stop-Service $svc -ErrorAction SilentlyContinue
    sc.exe delete $svc | Out-Null
    Write-Host "Служба '$svc' удалена." -ForegroundColor Green
} else {
    Write-Host "Служба '$svc' не найдена."
}
