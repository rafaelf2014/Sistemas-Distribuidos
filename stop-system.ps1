$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$pidFile = "$root\.running-pids"

if (Test-Path $pidFile) {
    Get-Content $pidFile | Where-Object { $_ -match '^\d+$' } | ForEach-Object {
        taskkill /F /T /PID $_ 2>$null | Out-Null
    }
    Remove-Item $pidFile -Force
} else {
    Get-Process dotnet, python, node -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

Write-Host 'Sistema parado.'
