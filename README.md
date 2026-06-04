# ONE HEALTH — Distributed Environmental Monitoring System

ONE HEALTH is a distributed system for real-time environmental monitoring. A network of
sensors spread across geographic zones continuously measures several environmental variables
(temperature, humidity, CO₂, noise, luminosity, particulate matter, NO₂, O₃ and wind). The
readings flow through edge **gateways** — which validate, normalize and analyze them — and on
to a central server that persists them and exposes them, through a REST API, to a web
application.

The system demonstrates the core concerns of a distributed architecture: heterogeneous
inter-process communication (message broker, gRPC, TCP, UDP and HTTP), edge computing, fault
tolerance with reconnection, machine-learning anomaly detection, and a topology that can run
on a single machine or be distributed across several.

---

## Architecture

```
[Sensors] ──AMQP──► [Gateways] ──gRPC──► [PreProcessamento]  (normalization)
                         |
                         │      ──gRPC──► [ServicoAnalise]    (ML + statistics)
                         |
                         │ TCP
                         ▼
                     [Server] ──► [PostgreSQL]
                         │ REST
                         ▼
                     [Frontend]  (browser)
```

The system is made up of six component types, supported by **RabbitMQ** (message broker) and
**PostgreSQL** (database):

| Component | Technology | Role |
|---|---|---|
| **Sensor** | C# / .NET | Generates and publishes readings in a configurable format |
| **Gateway** | C# / .NET | Edge node: buffers, validates, analyzes and forwards readings |
| **PreProcessamento** | C# / gRPC | Detects format, converts units, validates and scores quality |
| **ServicoAnalise** | Python / gRPC | Anomaly detection (ML), statistics, patterns and forecasting |
| **Server** | C# / .NET | Receives the data, persists it and exposes the REST API |
| **Frontend** | React / TypeScript | Web dashboard for visualization and control |

Each component is an instantiable class configured at startup — the same sensor or gateway
code serves any number of instances, distinguished only by their configuration folder.

---

## Communication Protocols

The system deliberately uses five different transports, each suited to its job:

| Hop | Transport | Why |
|---|---|---|
| Sensor → Gateway | RabbitMQ (AMQP, topic) | Decoupled pub/sub, zone-based routing |
| Gateway → PreProcessamento / Analysis | gRPC | Typed, efficient request/response over batches |
| Gateway → Server | TCP (JSON lines) | Simple, reliable, ordered stream of batches |
| Server → Gateway | TCP (command) | One-off requests (e.g. start video) |
| Sensor → Server | UDP (JPEG video) | Low latency, loss-tolerant |
| Server → Frontend | HTTP / REST (JSON) | Standard web-client integration |

Sensor readings can be sent in five formats (selected by configuration): **pipe**, **JSON**,
**XML**, **QueryString** and **Hexadecimal**. The pipe format is parsed directly by the
gateway; the others are forwarded to PreProcessamento, which handles format detection and
normalization.

---

## Features

- **Multi-format ingestion** — five wire formats, with automatic detection and unit conversion
- **Edge computing** — validation, normalization, scoring and alarms happen at the gateway, before central storage
- **ML anomaly detection** — one Isolation Forest model per data type, trained from history and retrained periodically
- **Analysis and forecasting** — per-zone statistics, pattern and trend detection, and short-horizon forecasting with a health-risk score
- **Alarm system** — thresholds configurable per zone and type, with a cooldown window
- **On-demand video streaming** — camera-capable sensors stream video over UDP, coordinated by the server and gateway
- **Fault tolerance** — automatic reconnection to the broker and server, a persisted retry queue, and a sensor watchdog with heartbeat-based recovery
- **Authentication** — REST API protected by JWT token

---

## Project Structure

```
/
├── Sensor/                 # Sensor code (one instance per config folder)
├── Gateway/                # Gateway code (one instance per config folder)
├── Server/                 # Central server (TCP + REST API)
├── PreProcessamento/       # gRPC normalization service
├── ServicoAnalise/         # gRPC analysis & ML service (Python)
├── Frontend/               # Web app (React + TypeScript)
├── protos/                 # Shared Protocol Buffers definitions
├── configs/
│   ├── gateways/           # Per-gateway configuration
│   └── sensors/            # Per-sensor configuration
└── start-*.ps1             # Startup scripts
```

A sensor or gateway runs by pointing it at its configuration folder at startup
(`dotnet run -- <config_folder>`), so adding a new instance is just a matter of creating a new
configuration folder — no code changes.

---

## Data Model

The PostgreSQL database has two main tables:

- **`sensores`** — registry of each sensor (zone, types, status, video capability, last sync)
- **`leituras`** — time series of all readings, with normalized value, quality, alarm flag and anomaly score

---

## Configuration

### Sensor (`configs/sensors/<id>/config_sensor.json`)
```json
{
  "sensorId": "S001",
  "zona": "ZONA_EXAMPLE",
  "zonaType": "residencial",
  "videoStream": false,
  "rabbitMqHost": "localhost",
  "formato": "pipe",
  "leituras": [
    { "tipo": "TEMP", "intervaloMs": 5000 },
    { "tipo": "HUM",  "intervaloMs": 5000 }
  ]
}
```
The `formato` field accepts `pipe`, `json`, `xml`, `querystring` or `hex`. Each reading may
declare an optional `unidade` (e.g. `"F"` for temperature in Fahrenheit), which
PreProcessamento converts to the canonical unit.

### Gateway (`configs/gateways/<id>/config_gateway.json`)
```json
{
  "gatewayId": "Gateway_001",
  "serverIp": "127.0.0.1",
  "rabbitMqHost": "localhost",
  "zonasSubscritas": [ "ZONA_A", "ZONA_B" ],
  "comandoPort": 14001,
  "tiposDados": [ ... ]
}
```

For distributed execution, the server and broker addresses can be injected via environment
variables (`SERVER_IP`, `RABBITMQ_HOST`, `ANALISE_GRPC_URL`, `PREPROCESSAMENTO_GRPC_URL`),
which take precedence over the configuration files.

---

## Running

**Prerequisites:** RabbitMQ and PostgreSQL running; .NET SDK; Python with the analysis
service's dependencies; Node.js for the frontend.

### Single machine
```powershell
.\start-system.ps1
```
Builds and starts every component (services, server, gateways, sensors and frontend), each in
its own window.

### Distributed across machines
```powershell
.\start-pc1.ps1 -Pc3Ip <server-ip>
.\start-pc2.ps1 -Pc3Ip <server-ip>
.\start-pc3.ps1
```
The scripts distribute the components across hosts, injecting the addresses via environment
variables.

### Stop
```powershell
.\stop-system.ps1
```

---

## Ports

| Service | Port |
|---|---|
| RabbitMQ (AMQP / management) | 5672 / 15672 |
| PreProcessamento (gRPC) | 50051 |
| ServicoAnalise (gRPC) | 50052 |
| Server (data TCP) | 14000 |
| Server (REST API) | 8080 |
| Gateway (command TCP) | 14001+ |
| Video (UDP) | 15000 |
| Frontend (dev) | 5173 |

---

## Message Protocol (pipe format)

| Message | Direction | Description |
|---|---|---|
| `HELLO\|id\|zone\|[types]\|video` | Sensor → Gateway | Initial registration |
| `DATA_SEND\|id\|type\|value\|ts[\|unit]` | Sensor → Gateway | Reading |
| `HEARTBEAT\|id` | Sensor → Gateway | Liveness signal |
| `BYE\|id` | Sensor → Gateway | Graceful shutdown |
| `DATA_BATCH` (JSON) | Gateway → Server | Batch of normalized readings |
| `SENSOR_REG` (JSON) | Gateway → Server | Sensor registration |
| `SENSOR_STATUS` (JSON) | Gateway → Server | Sensor state change |
| `REQUEST_STREAM\|id\|ip\|port` | Server → Gateway | Video stream request |
| `STREAM_TO\|ip:port` / `STOP_STREAM` | Gateway → Sensor | Start / stop video |

---

## Technologies

- **C# / .NET** — sensors, gateways, server and pre-processing
- **Python** — analysis service (gRPC, pandas, numpy, scikit-learn)
- **React + TypeScript** — frontend (with Recharts)
- **RabbitMQ** — message broker (AMQP)
- **gRPC / Protocol Buffers** — inter-service communication
- **PostgreSQL** — persistence (time series)
- **OpenCV** — video capture and display, over UDP
- **JWT** — REST API authentication

---

> This repository ships with a demonstration configuration (several pre-configured zones and
> sensors) that exists only to showcase the system in action. The architecture and components
> described above are independent of that particular configuration.
