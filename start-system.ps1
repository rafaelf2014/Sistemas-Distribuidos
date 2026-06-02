$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pids = @()

$pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root\PreProcessamento'; dotnet run" -PassThru).Id
Start-Sleep 3

$pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root\ServicoAnalise'; python server.py" -PassThru).Id
Start-Sleep 5

$pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root\Server'; dotnet run" -PassThru).Id
Start-Sleep 4

$pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root\Gateway_001'; dotnet run" -PassThru).Id
Start-Sleep 3

$pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root\Sensor_001'; dotnet run" -PassThru).Id
$pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root\Sensor_002'; dotnet run" -PassThru).Id
$pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root\Sensor_003'; dotnet run" -PassThru).Id
$pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root\Sensor_004'; dotnet run" -PassThru).Id
Start-Sleep 2

$pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root\Frontend'; npm run dev" -PassThru).Id

$pids | Out-File "$root\.running-pids" -Encoding utf8
Write-Host 'Sistema iniciado. Para parar: .\stop-system.ps1'
