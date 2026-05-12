@echo off
REM ============================================================
REM  ONE HEALTH - PC2 (Gateway_001 + Sensors S001-S003)
REM  ANTES DE CORRER: editar serverIp em Gateway_001\config_gateway.json
REM  com o IP real do PC1 (servidor).
REM ============================================================

set ROOT=%~dp0

start "Gateway_001"  cmd /k "cd /d %ROOT%Gateway_001  && dotnet run"
timeout /t 3 /nobreak >nul

start "Sensor S001"  cmd /k "cd /d %ROOT%Sensor_001  && dotnet run -- 127.0.0.1"
start "Sensor S002"  cmd /k "cd /d %ROOT%Sensor_002  && dotnet run -- 127.0.0.1"
start "Sensor S003"  cmd /k "cd /d %ROOT%Sensor_003  && dotnet run -- 127.0.0.1"
