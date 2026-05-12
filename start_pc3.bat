@echo off
REM ============================================================
REM  ONE HEALTH - PC3 (Gateway_002 + Sensors S011-S013)
REM  ANTES DE CORRER: editar serverIp em Gateway_002\config_gateway.json
REM  com o IP real do PC1 (servidor).
REM ============================================================

set ROOT=%~dp0

start "Gateway_002"  cmd /k "cd /d %ROOT%Gateway_002  && dotnet run"
timeout /t 3 /nobreak >nul

start "Sensor S011"  cmd /k "cd /d %ROOT%Sensor_011  && dotnet run -- 127.0.0.1"
start "Sensor S012"  cmd /k "cd /d %ROOT%Sensor_012  && dotnet run -- 127.0.0.1"
start "Sensor S013"  cmd /k "cd /d %ROOT%Sensor_013  && dotnet run -- 127.0.0.1"
