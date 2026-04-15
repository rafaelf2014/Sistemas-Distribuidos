# Sistema de Monitorização Ambiental Distribuído - DEMO

Projecto desenvolvido no âmbito da unidade curricular de **Sistemas Distribuídos**.  
Simula uma rede de sensores ambientais distribuídos por duas cidades, com processamento de dados em três camadas: sensores, gateways de borda e servidor central.

---

## Arquitectura

```
[Sensores]  ──TCP──►  [Gateway Edge]  ──TCP──►  [Servidor Central]
                                                        │
                                                   [Base de Dados SQLite]
```

O sistema é composto por três tipos de componentes, distribuídos por três máquinas:

| Máquina | Componentes | Cidade |
|---|---|---|
| PC1 | Servidor Central | — |
| PC2 | Gateway_001 + Sensores S001–S010 | Chaves |
| PC3 | Gateway_002 + Sensores S011–S020 | Vila Real |

---

## Funcionalidades

- **Recolha de dados em tempo real** — temperatura, humidade, CO₂ e ruído
- **Edge Analytics** — detecção de anomalias e geração de alarmes no gateway, sem depender do servidor
- **Agregação de dados** — o gateway agrega leituras por janela temporal antes de as enviar ao servidor
- **Tolerância a falhas** — dados em buffer local (CSV) enquanto o servidor não está disponível; watchdog detecta sensores perdidos
- **Streaming de vídeo** — sensores S001 e S002 transmitem vídeo via UDP directamente ao servidor (iniciado pelo operador na TUI do servidor)
- **Persistência** — todos os dados e alarmes são guardados numa base de dados SQLite no servidor
- **TUI sem flicker** — dashboards em consola para servidor, gateways e sensores

---

## Estrutura do Projecto

```
/
├── Server/                  # Servidor central (PC1)
├── Gateway_001/             # Gateway de borda — Chaves (PC2)
├── Gateway_002/             # Gateway de borda — Vila Real (PC3)
├── Sensor_001/ … Sensor_003/  # Sensores com capacidade de vídeo (PC2)
├── Sensor_004/ … Sensor_010/  # Sensores padrão — Chaves (PC2)
├── Sensor_011/ … Sensor_020/  # Sensores padrão — Vila Real (PC3)
├── start_pc2.bat            # Script de arranque para PC2
└── start_pc3.bat            # Script de arranque para PC3
```

---

## Configuração

### 1. Definir o IP do servidor nos gateways

Antes de executar, editar o campo `serverIp` em ambos os ficheiros de configuração dos gateways com o IP real do PC1:

**`Gateway_001/config_gateway.json`** e **`Gateway_002/config_gateway.json`**:
```json
{
  "gatewayId": "Gateway_001",
  "serverIp": "192.168.X.X",
  ...
}
```

### 2. Configuração dos sensores

Cada sensor tem o seu próprio `config_sensor.json`:
```json
{
  "sensorId": "S001",
  "zona": "CHAVES_NORTE",
  "videoStream": true,
  "leituras": [
    { "tipo": "TEMP", "intervaloMs": 5000 },
    { "tipo": "HUM",  "intervaloMs": 5000 }
  ]
}
```

Tipos de dados suportados: `TEMP`, `HUM`, `CO2`, `RUIDO`.  
Para adicionar um novo tipo, basta adicioná-lo ao `config_sensor.json` e ao `config_gateway.json` do respectivo gateway.

---

## Como Executar

### PC1 — Servidor
```bash
cd Server
dotnet run
```

### PC2 — Gateway + Sensores Chaves
```bat
start_pc2.bat
```
Abre automaticamente o Gateway_001 e os 10 sensores, cada um na sua própria janela.

### PC3 — Gateway + Sensores Vila Real
```bat
start_pc3.bat
```
Abre automaticamente o Gateway_002 e os 10 sensores, cada um na sua própria janela.

> Os sensores ligam-se sempre ao gateway local (`127.0.0.1`). Apenas o IP do servidor precisa de ser configurado.

---

## Streaming de Vídeo

1. No dashboard do servidor, os sensores com capacidade de vídeo aparecem numerados
2. Premir a tecla correspondente (`1`–`5`) inicia o stream
3. Uma janela OpenCV abre com o feed em tempo real (UDP, 320×240, ~30fps)
4. Premir a mesma tecla ou `ESC` termina o stream

---

## Tecnologias

- **C# / .NET 9**
- **TCP/IP** — comunicação sensores↔gateway e gateway↔servidor
- **UDP** — streaming de vídeo
- **SQLite** — persistência de dados e alarmes (`Microsoft.Data.Sqlite`)
- **OpenCvSharp4** — captura e visualização de vídeo (apenas sensores com vídeo e servidor)

---

## Protocolo de Mensagens

| Mensagem | Sentido | Descrição |
|---|---|---|
| `HELLO\|id\|zona\|[tipos]\|video` | Sensor → Gateway | Registo inicial |
| `DATA_SEND\|id\|tipo\|valor\|ts` | Sensor → Gateway | Envio de leitura |
| `HEARTBEAT\|id` | Sensor → Gateway | Sinal de vida |
| `BYE\|id` | Sensor → Gateway | Desligamento gracioso |
| `DATA_FORWARD\|gw\|id\|zona\|tipo\|valor\|ts` | Gateway → Servidor | Reencaminhamento de dados |
| `ALARM_FORWARD\|...` | Gateway → Servidor | Reencaminhamento de alarme |
| `SENSOR_REG\|gw\|id\|zona\|tipos\|video` | Gateway → Servidor | Registo de sensor |
| `REQUEST_STREAM\|id\|ip\|porta` | Servidor → Gateway | Pedido de stream de vídeo |
| `STREAM_TO\|ip:porta` | Gateway → Sensor (piggyback) | Instrução para iniciar stream |
| `STOP_STREAM` | Gateway → Sensor (piggyback) | Instrução para parar stream |
