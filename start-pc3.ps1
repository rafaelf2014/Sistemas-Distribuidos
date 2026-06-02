# ONE HEALTH — PC3 (Servidor + Analise + Frontend)
# Requer: RabbitMQ e PostgreSQL a correr localmente
# Uso: .\start-pc3.ps1

$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Launch {
    param([string]$Name, [string]$Dir, [string]$Cmd)
    $inner = "`$host.UI.RawUI.WindowTitle = 'PC3 | $Name'; cd '$Dir'; $Cmd"
    $p = Start-Process powershell -ArgumentList "-NoExit", "-Command", $inner -PassThru
    return $p.Id
}

function Step { param([string]$Msg) Write-Host "  $Msg" -ForegroundColor Green }

Write-Host ""
Write-Host "  ╔══════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "  ║    ONE HEALTH — PC3 (Servidor)       ║" -ForegroundColor Cyan
Write-Host "  ╚══════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "  AVISO: RabbitMQ e PostgreSQL devem estar a correr." -ForegroundColor Yellow
Write-Host ""

$pids = @()

Write-Host "  [1/3] ServicoAnalise (Python)" -ForegroundColor Yellow
$pids += Launch "ServicoAnalise" "$root\ServicoAnalise" "python server.py"
Step "ServicoAnalise    (porta 50052)"
Start-Sleep -Seconds 4

Write-Host ""
Write-Host "  [2/3] Servidor Central" -ForegroundColor Yellow
$pids += Launch "Server" "$root\Server" "dotnet run"
Step "Server            (TCP 14000 / API 8080)"
Start-Sleep -Seconds 4

Write-Host ""
Write-Host "  [3/3] Frontend" -ForegroundColor Yellow
$pids += Launch "Frontend" "$root\Frontend" "npm run dev"
Step "Frontend          (http://localhost:5173)"

$pids | Out-File "$root\.running-pids" -Encoding utf8

Write-Host ""
Write-Host "  ╔══════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "  ║         PC3 iniciado.                ║" -ForegroundColor Cyan
Write-Host "  ╚══════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Frontend  ->  http://localhost:5173" -ForegroundColor White
Write-Host "  API REST  ->  http://localhost:8080" -ForegroundColor White
Write-Host "  RabbitMQ  ->  http://localhost:15672" -ForegroundColor White
Write-Host ""
Write-Host "  Para parar: .\stop-system.ps1" -ForegroundColor DarkGray
Write-Host ""
