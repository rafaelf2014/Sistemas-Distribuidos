param([string]$Pc3Ip = '127.0.0.1')

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pids = @()

$envs = "`$env:RABBITMQ_HOST='$Pc3Ip'; `$env:SERVER_IP='$Pc3Ip'; `$env:ANALISE_GRPC_URL='http://${Pc3Ip}:50052'; `$env:PREPROCESSAMENTO_GRPC_URL='http://localhost:50051'"

$pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root\PreProcessamento'; dotnet run" -PassThru).Id
Start-Sleep 3

$pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "$envs; cd '$root\Gateway'; dotnet run -- '$root\configs\gateways\Gateway_002'" -PassThru).Id
Start-Sleep 3

foreach ($n in 11..20) {
    $cfg = "$root\configs\sensors\Sensor_" + $n.ToString("D3")
    $pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "$envs; cd '$root\Sensor'; dotnet run -- '$cfg'" -PassThru).Id
}

$pids | Out-File "$root\.running-pids" -Encoding utf8
Write-Host "PC2 iniciado. Para parar: .\stop-system.ps1"
