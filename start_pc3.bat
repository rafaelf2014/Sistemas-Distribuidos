@echo off
REM ============================================================
REM  ONE HEALTH - PC3 (Gateway_002 + Sensors S011-S020)
REM  ANTES DE CORRER: editar serverIp em Gateway_002\config_gateway.json
REM  com o IP real do PC1 (servidor).
REM ============================================================

set ROOT=%~dp0

start "Gateway_002"  cmd /k "cd /d %ROOT%Gateway_002  && dotnet run"
timeout /t 3 /nobreak >nul

start "Sensor S011"  cmd /k "cd /d %ROOT%Sensor_011  && dotnet run -- 127.0.0.1"
start "Sensor S012"  cmd /k "cd /d %ROOT%Sensor_012  && dotnet run -- 127.0.0.1"
start "Sensor S013"  cmd /k "cd /d %ROOT%Sensor_013  && dotnet run -- 127.0.0.1"
start "Sensor S014"  cmd /k "cd /d %ROOT%Sensor_014  && dotnet run -- 127.0.0.1"
start "Sensor S015"  cmd /k "cd /d %ROOT%Sensor_015  && dotnet run -- 127.0.0.1"
start "Sensor S016"  cmd /k "cd /d %ROOT%Sensor_016  && dotnet run -- 127.0.0.1"
start "Sensor S017"  cmd /k "cd /d %ROOT%Sensor_017  && dotnet run -- 127.0.0.1"
start "Sensor S018"  cmd /k "cd /d %ROOT%Sensor_018  && dotnet run -- 127.0.0.1"
start "Sensor S019"  cmd /k "cd /d %ROOT%Sensor_019  && dotnet run -- 127.0.0.1"
start "Sensor S020"  cmd /k "cd /d %ROOT%Sensor_020  && dotnet run -- 127.0.0.1"
