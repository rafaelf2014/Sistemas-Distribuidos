@echo off
echo [ONE HEALTH] A iniciar todos os servicos...

start "PreProcessamento" cmd /k "cd /d %~dp0PreProcessamento && dotnet run"
start "ServicoAnalise"   cmd /k "cd /d %~dp0ServicoAnalise   && python server.py"
start "Server"           cmd /k "cd /d %~dp0Server           && dotnet run"

timeout /t 4 /nobreak >nul

start "Gateway_001"      cmd /k "cd /d %~dp0Gateway_001      && dotnet run"

timeout /t 2 /nobreak >nul

start "Sensor_001"       cmd /k "cd /d %~dp0Sensor_001       && dotnet run"
start "Frontend"         cmd /k "cd /d %~dp0Frontend         && npm run dev"

echo [ONE HEALTH] Todos os servicos iniciados.
echo Frontend disponivel em http://localhost:5173
