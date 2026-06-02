$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pids = @()

$pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root\ServicoAnalise'; python server.py" -PassThru).Id
Start-Sleep 5

$pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root\Server'; dotnet run" -PassThru).Id
Start-Sleep 4

$pids += (Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$root\Frontend'; npm run dev" -PassThru).Id

$pids | Out-File "$root\.running-pids" -Encoding utf8
Write-Host "PC3 iniciado. Para parar: .\stop-system.ps1"
