$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pids = @()

$pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root\PreProcessamento'; dotnet run" -PassThru).Id
Start-Sleep 3

$pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root\ServicoAnalise'; python server.py" -PassThru).Id
Start-Sleep 5

$pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root\Server'; dotnet run" -PassThru).Id
Start-Sleep 4

$pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root\Gateway'; dotnet run -- '$root\configs\gateways\Gateway_001'" -PassThru).Id
$pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root\Gateway'; dotnet run -- '$root\configs\gateways\Gateway_002'" -PassThru).Id
Start-Sleep 3

foreach ($n in 1..20) {
    $cfg = "$root\configs\sensors\Sensor_" + $n.ToString("D3")
    $pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root\Sensor'; dotnet run -- '$cfg'" -PassThru).Id
}
Start-Sleep 2

$pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root\Frontend'; npm run dev" -PassThru).Id

$pids | Out-File "$root\.running-pids" -Encoding utf8
Write-Host 'Sistema iniciado. Para parar: .\stop-system.ps1'
