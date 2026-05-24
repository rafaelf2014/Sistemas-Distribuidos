# ONE HEALTH — Stop All Services
# Run from the project root: .\stop-system.ps1

$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$pidFile = "$root\.running-pids"

Write-Host ""
Write-Host "  Stopping ONE HEALTH..." -ForegroundColor Yellow

if (Test-Path $pidFile) {
    $savedPids = Get-Content $pidFile | Where-Object { $_ -match '^\d+$' }
    foreach ($id in $savedPids) {
        try {
            # Kill the entire process tree (the powershell host + its children)
            taskkill /F /T /PID $id 2>$null | Out-Null
        } catch {}
    }
    Remove-Item $pidFile -Force
    Write-Host "  Todos os processos terminados." -ForegroundColor Green
} else {
    Write-Host "  Ficheiro de PIDs nao encontrado — a tentar por nome..." -ForegroundColor DarkYellow
    # Fallback: kill by process name (best effort)
    @("dotnet", "python", "node") | ForEach-Object {
        Get-Process $_ -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Write-Host "  Processos dotnet/python/node terminados." -ForegroundColor Green
}

Write-Host ""
