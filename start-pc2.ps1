# ONE HEALTH — PC2 (Gateway_002 + Sensores S011-S014)
# Uso: .\start-pc2.ps1 -Pc3Ip "192.168.1.X"

param([string]$Pc3Ip = "127.0.0.1")

$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$env:RABBITMQ_HOST             = $Pc3Ip
$env:SERVER_IP                 = $Pc3Ip
$env:ANALISE_GRPC_URL          = "http://${Pc3Ip}:50052"
$env:PREPROCESSAMENTO_GRPC_URL = "http://localhost:50051"

function Launch {
    param([string]$Name, [string]$Dir, [string]$Cmd)
    $inner = "`$host.UI.RawUI.WindowTitle = 'PC2 | $Name'; cd '$Dir'; $Cmd"
    $p = Start-Process powershell -ArgumentList "-NoExit", "-Command", $inner -PassThru
    return $p.Id
}

function Step { param([string]$Msg) Write-Host "  $Msg" -ForegroundColor Green }

Write-Host ""
Write-Host "  ╔══════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "  ║    ONE HEALTH — PC2                  ║" -ForegroundColor Cyan
Write-Host "  ║    PC3 (servidor): $Pc3Ip" -ForegroundColor Cyan
Write-Host "  ╚══════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

$pids = @()

Write-Host "  [1/3] PreProcessamento" -ForegroundColor Yellow
$pids += Launch "PreProcessamento" "$root\PreProcessamento" "dotnet run"
Step "PreProcessamento  (porta 50051)"
Start-Sleep -Seconds 3

Write-Host ""
Write-Host "  [2/3] Gateway_002" -ForegroundColor Yellow
$pids += Launch "Gateway_002" "$root\Gateway_002" "dotnet run"
Step "Gateway_002  ->  RabbitMQ @ $Pc3Ip"
Start-Sleep -Seconds 3

Write-Host ""
Write-Host "  [3/3] Sensores S011-S020" -ForegroundColor Yellow
foreach ($n in 11..20) {
    $id = "S0$n"
    $dir = "Sensor_0$n"
    $pids += Launch "Sensor $id" "$root\$dir" "dotnet run"
    Step "$dir iniciado"
}

$pids | Out-File "$root\.running-pids" -Encoding utf8

Write-Host ""
Write-Host "  ╔══════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "  ║  PC2 iniciado (GW2 + S011-020) -> $Pc3Ip" -ForegroundColor Cyan
Write-Host "  ╚══════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Para parar: .\stop-system.ps1" -ForegroundColor DarkGray
Write-Host ""
