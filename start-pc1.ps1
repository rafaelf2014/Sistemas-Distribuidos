# ONE HEALTH — PC1 (Gateway_001 + Sensores S001-S004)
# Uso: .\start-pc1.ps1 -Pc3Ip "192.168.1.X"

param([string]$Pc3Ip = "127.0.0.1")

$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# Injetar IPs via variáveis de ambiente (lidas pelo Gateway e Sensores)
$env:RABBITMQ_HOST            = $Pc3Ip
$env:SERVER_IP                = $Pc3Ip
$env:ANALISE_GRPC_URL         = "http://${Pc3Ip}:50052"
$env:PREPROCESSAMENTO_GRPC_URL = "http://localhost:50051"

function Launch {
    param([string]$Name, [string]$Dir, [string]$Cmd)
    $inner = "`$host.UI.RawUI.WindowTitle = 'PC1 | $Name'; cd '$Dir'; $Cmd"
    $p = Start-Process powershell -ArgumentList "-NoExit", "-Command", $inner -PassThru
    return $p.Id
}

function Step { param([string]$Msg) Write-Host "  $Msg" -ForegroundColor Green }

Write-Host ""
Write-Host "  ╔══════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "  ║    ONE HEALTH — PC1                  ║" -ForegroundColor Cyan
Write-Host "  ║    PC3 (servidor): $Pc3Ip" -ForegroundColor Cyan
Write-Host "  ╚══════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

$pids = @()

Write-Host "  [1/3] PreProcessamento" -ForegroundColor Yellow
$pids += Launch "PreProcessamento" "$root\PreProcessamento" "dotnet run"
Step "PreProcessamento  (porta 50051)"
Start-Sleep -Seconds 3

Write-Host ""
Write-Host "  [2/3] Gateway_001" -ForegroundColor Yellow
$pids += Launch "Gateway_001" "$root\Gateway_001" "dotnet run"
Step "Gateway_001  ->  RabbitMQ @ $Pc3Ip"
Start-Sleep -Seconds 3

Write-Host ""
Write-Host "  [3/3] Sensores S001-S010" -ForegroundColor Yellow
foreach ($n in 1..9) {
    $id = "S00$n"
    $dir = "Sensor_00$n"
    $pids += Launch "Sensor $id" "$root\$dir" "dotnet run"
    Step "$dir iniciado"
}
$pids += Launch "Sensor S010" "$root\Sensor_010" "dotnet run"
Step "Sensor_010 iniciado"

$pids | Out-File "$root\.running-pids" -Encoding utf8

Write-Host ""
Write-Host "  ╔══════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "  ║  PC1 iniciado (GW1 + S001-010) -> $Pc3Ip" -ForegroundColor Cyan
Write-Host "  ╚══════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Para parar: .\stop-system.ps1" -ForegroundColor DarkGray
Write-Host ""
