# ONE HEALTH — Start All Services
# Run from the project root: .\start-system.ps1

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pids = @()

function Launch {
    param([string]$Name, [string]$Dir, [string]$Cmd)
    $inner = "`$host.UI.RawUI.WindowTitle = 'ONE HEALTH | $Name'; cd '$Dir'; $Cmd"
    $p = Start-Process powershell -ArgumentList "-NoExit", "-Command", $inner -PassThru
    return $p.Id
}

function Step {
    param([string]$Msg)
    Write-Host "  $Msg" -ForegroundColor Green
}

Write-Host ""
Write-Host "  ╔══════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "  ║        ONE HEALTH — ARRANQUE         ║" -ForegroundColor Cyan
Write-Host "  ╚══════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

# ── Layer 1: gRPC services ──────────────────────────────
Write-Host "  [1/5] Serviços gRPC" -ForegroundColor Yellow

$pids += Launch "PreProcessamento" "$root\PreProcessamento" "dotnet run"
Step "PreProcessamento iniciado  (porta 50051)"
Start-Sleep -Seconds 1

$pids += Launch "ServicoAnalise" "$root\ServicoAnalise" "python server.py"
Step "ServicoAnalise iniciado    (porta 50052)"
Start-Sleep -Seconds 4   # give dotnet/python time to bind

# ── Layer 2: Central server ────────────────────────────
Write-Host ""
Write-Host "  [2/5] Servidor Central" -ForegroundColor Yellow

$pids += Launch "Server" "$root\Server" "dotnet run"
Step "Server iniciado            (TCP 14000, API 8080)"
Start-Sleep -Seconds 4

# ── Layer 3: Gateways ──────────────────────────────────
Write-Host ""
Write-Host "  [3/5] Gateways" -ForegroundColor Yellow

$pids += Launch "Gateway_001" "$root\Gateway_001" "dotnet run"
Step "Gateway_001 iniciado       (AMQP / porta 14001)"
Start-Sleep -Seconds 1

$pids += Launch "Gateway_002" "$root\Gateway_002" "dotnet run"
Step "Gateway_002 iniciado       (TCP 5000 / porta 14001)"
Start-Sleep -Seconds 3

# ── Layer 4: Sensors ───────────────────────────────────
Write-Host ""
Write-Host "  [4/5] Sensores" -ForegroundColor Yellow

$sensors = @("Sensor_001","Sensor_002","Sensor_003","Sensor_011","Sensor_012","Sensor_013")
foreach ($s in $sensors) {
    $pids += Launch $s "$root\$s" "dotnet run"
    Step "$s iniciado"
    Start-Sleep -Milliseconds 800
}
Start-Sleep -Seconds 2

# ── Layer 5: Frontend ──────────────────────────────────
Write-Host ""
Write-Host "  [5/5] Frontend" -ForegroundColor Yellow

$pids += Launch "Frontend" "$root\Frontend" "npm run dev"
Step "Frontend iniciado          (http://localhost:5173)"

# ── Save PIDs for stop script ──────────────────────────
$pids | Out-File -FilePath "$root\.running-pids" -Encoding utf8

Write-Host ""
Write-Host "  ╔══════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "  ║  Sistema iniciado. PIDs guardados.   ║" -ForegroundColor Cyan
Write-Host "  ╚══════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Frontend  ->  http://localhost:5173" -ForegroundColor White
Write-Host "  API REST  ->  http://localhost:8080" -ForegroundColor White
Write-Host "  RabbitMQ  ->  http://localhost:15672" -ForegroundColor White
Write-Host ""
Write-Host "  Para parar tudo: .\stop-system.ps1" -ForegroundColor DarkGray
Write-Host ""