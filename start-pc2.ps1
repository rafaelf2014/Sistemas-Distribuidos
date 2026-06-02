param([string]$Pc3Ip = '127.0.0.1')

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pids = @()

$env:RABBITMQ_HOST             = $Pc3Ip
$env:SERVER_IP                 = $Pc3Ip
$env:ANALISE_GRPC_URL          = "http://${Pc3Ip}:50052"
$env:PREPROCESSAMENTO_GRPC_URL = 'http://localhost:50051'

$pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root\PreProcessamento'; dotnet run" -PassThru).Id
Start-Sleep 3

$pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root\Gateway_002'; dotnet run" -PassThru).Id
Start-Sleep 3

foreach ($n in 11..20) {
    $pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root\Sensor_0$n'; dotnet run" -PassThru).Id
}

$pids | Out-File "$root\.running-pids" -Encoding utf8
Write-Host "PC2 iniciado. Para parar: .\stop-system.ps1"
