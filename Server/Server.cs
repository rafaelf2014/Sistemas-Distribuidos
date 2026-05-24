using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;

partial class ServerCentral
{
    #region CAMPOS

    private static readonly string connectionString =
        Environment.GetEnvironmentVariable("DATABASE_URL")
        ?? "Host=localhost;Database=one_health;Username=postgres;Password=postgres";

    static TcpListener? _server = null;

    private static readonly ConcurrentDictionary<string, (string GatewayId, string Zona, string Tipos)> _sensoresStream = new();

    #endregion

    #region INICIALIZAÇÃO

    public static async Task Main()
    {
        Console.CancelKeyPress += TratarEncerramento;
        InicializarBaseDeDados();
        IniciarApi();

        try
        {
            const int port = 14000;
            _server = new TcpListener(IPAddress.Any, port);
            _server.Start();

            RegistarLog($"Servidor à escuta na porta {port}...");
            RegistarLog($"Base de dados: {connectionString.Split(';')[0]}");

            while (_isOnline)
            {
                TcpClient client = await _server.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleGatewayAsync(client));
            }
        }
        catch (SocketException) { RegistarLog("Escuta interrompida."); }
        finally { _server?.Stop(); }
    }

    #endregion

    #region HANDLER DE GATEWAYS

    static async Task HandleGatewayAsync(TcpClient gatewayClient)
    {
        string endpoint  = gatewayClient.Client.RemoteEndPoint?.ToString() ?? "?";
        string gatewayIp = ((IPEndPoint)gatewayClient.Client.RemoteEndPoint!).Address.ToString();

        try
        {
            gatewayClient.ReceiveTimeout = 120_000;
            var stream = gatewayClient.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            string? linha;
            while ((linha = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(linha)) continue;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(linha); }
                catch
                {
                    await writer.WriteLineAsync("{\"tipo\":\"ACK\",\"status\":\"ERRO\",\"erro\":\"JSON invalido\"}");
                    continue;
                }

                using (doc)
                {
                    string tipo      = doc.RootElement.TryGetProperty("tipo",      out var t) ? t.GetString() ?? "" : "";
                    string gatewayId = doc.RootElement.TryGetProperty("gatewayId", out var g) ? g.GetString() ?? "" : "";

                    if (!string.IsNullOrEmpty(gatewayId))
                        _gatewayIps[gatewayId] = gatewayIp;

                    string ack = tipo switch
                    {
                        "DATA_BATCH"    => await HandleDataBatch(doc.RootElement, gatewayId),
                        "ALARM_FORWARD" => await HandleAlarmForward(doc.RootElement, gatewayId),
                        "SENSOR_REG"    => await HandleSensorReg(doc.RootElement, gatewayId),
                        "SENSOR_STATUS" => await HandleSensorStatus(doc.RootElement),
                        _               => $"{{\"tipo\":\"ACK\",\"status\":\"ERRO\",\"erro\":\"Tipo desconhecido: {tipo}\"}}"
                    };

                    await writer.WriteLineAsync(ack);
                }
            }
        }
        catch (Exception e) { RegistarLog($"ERRO REDE ({endpoint}): {e.Message}"); }
        finally { gatewayClient.Close(); }
    }

    static async Task<string> HandleDataBatch(JsonElement root, string gatewayId)
    {
        if (!root.TryGetProperty("leituras", out var leituras) || leituras.ValueKind != JsonValueKind.Array)
            return "{\"tipo\":\"ACK_BATCH\",\"status\":\"ERRO\",\"erro\":\"Campo leituras ausente\"}";

        int count = 0;
        try
        {
            await using var conn  = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var batch = new NpgsqlBatch(conn);

            foreach (var l in leituras.EnumerateArray())
            {
                string sensorId  = l.TryGetProperty("sensorId",  out var s)  ? s.GetString()  ?? "" : "";
                string zona      = l.TryGetProperty("zona",      out var z)  ? z.GetString()  ?? "" : "";
                string tipoDado  = l.TryGetProperty("tipoDado",  out var td) ? td.GetString() ?? "" : "";
                double valor     = l.TryGetProperty("valor",     out var v)  ? v.GetDouble()  : 0;
                string tsStr     = l.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? "" : "";
                double qualidade = l.TryGetProperty("qualidade", out var q)  ? q.GetDouble()  : 1.0;
                bool   isAlarm   = l.TryGetProperty("isAlarm",   out var ia) && ia.GetBoolean();

                DateTime timestamp = DateTime.TryParse(tsStr, out DateTime dt) ? dt : DateTime.Now;

                if (isAlarm) RegistarLog($"[{zona}] ANOMALIA! Sensor: {sensorId} | {tipoDado} = {valor}", true);

                var cmd = new NpgsqlBatchCommand(
                    "INSERT INTO leituras (sensor_id, gateway_id, zona, tipo_dado, valor, timestamp, is_alarm, qualidade) " +
                    "VALUES ($1, $2, $3, $4, $5, $6, $7, $8)");
                cmd.Parameters.Add(new NpgsqlParameter { Value = sensorId });
                cmd.Parameters.Add(new NpgsqlParameter { Value = gatewayId });
                cmd.Parameters.Add(new NpgsqlParameter { Value = zona });
                cmd.Parameters.Add(new NpgsqlParameter { Value = tipoDado });
                cmd.Parameters.Add(new NpgsqlParameter { Value = valor });
                cmd.Parameters.Add(new NpgsqlParameter { Value = timestamp });
                cmd.Parameters.Add(new NpgsqlParameter { Value = isAlarm });
                cmd.Parameters.Add(new NpgsqlParameter { Value = (float)qualidade });
                batch.BatchCommands.Add(cmd);
                count++;

                RegistarLog($"[{zona}] {sensorId} → {tipoDado} = {valor}");
            }

            if (count > 0)
                await batch.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            RegistarLog($"Erro batch INSERT: {ex.Message}");
            return $"{{\"tipo\":\"ACK_BATCH\",\"status\":\"ERRO\",\"erro\":\"{ex.Message.Replace("\"", "'")}\"}}";
        }

        return $"{{\"tipo\":\"ACK_BATCH\",\"status\":\"OK\",\"count\":{count}}}";
    }

    static async Task<string> HandleAlarmForward(JsonElement root, string gatewayId)
    {
        string sensorId = root.TryGetProperty("sensorId",  out var s)  ? s.GetString()  ?? "" : "";
        string zona     = root.TryGetProperty("zona",      out var z)  ? z.GetString()  ?? "" : "";
        string tipoDado = root.TryGetProperty("tipoDado",  out var td) ? td.GetString() ?? "" : "";
        double valor    = root.TryGetProperty("valor",     out var v)  ? v.GetDouble()  : 0;
        string tsStr    = root.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? "" : "";

        DateTime timestamp = DateTime.TryParse(tsStr, out DateTime dt) ? dt : DateTime.Now;

        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO leituras (sensor_id, gateway_id, zona, tipo_dado, valor, timestamp, is_alarm, qualidade) " +
                "VALUES ($1, $2, $3, $4, $5, $6, TRUE, 1.0)";
            cmd.Parameters.Add(new NpgsqlParameter { Value = sensorId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = gatewayId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = zona });
            cmd.Parameters.Add(new NpgsqlParameter { Value = tipoDado });
            cmd.Parameters.Add(new NpgsqlParameter { Value = valor });
            cmd.Parameters.Add(new NpgsqlParameter { Value = timestamp });
            await cmd.ExecuteNonQueryAsync();
            RegistarLog($"[{zona}] ANOMALIA! Sensor: {sensorId} | {tipoDado} = {valor}", true);
        }
        catch (Exception ex)
        {
            RegistarLog($"Erro alarme INSERT: {ex.Message}");
            return "{\"tipo\":\"ACK_ALARM\",\"status\":\"ERRO\"}";
        }

        return "{\"tipo\":\"ACK_ALARM\",\"status\":\"OK\"}";
    }

    static async Task<string> HandleSensorReg(JsonElement root, string gatewayId)
    {
        string sensorId    = root.TryGetProperty("sensorId",    out var s)  ? s.GetString()  ?? "" : "";
        string zona        = root.TryGetProperty("zona",        out var z)  ? z.GetString()  ?? "" : "";
        string tipos       = root.TryGetProperty("tipos",       out var td) ? td.GetString() ?? "" : "";
        bool   videoStream = root.TryGetProperty("videoStream", out var vs) ? vs.GetBoolean() : false;

        if (videoStream)
            _sensoresStream[sensorId] = (gatewayId, zona, tipos);

        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO sensores (sensor_id, gateway_id, zona, tipos, video_stream, status, ultima_sync)
                VALUES ($1, $2, $3, $4, $5, 'online', NOW())
                ON CONFLICT (sensor_id) DO UPDATE SET
                    gateway_id   = EXCLUDED.gateway_id,
                    zona         = EXCLUDED.zona,
                    tipos        = EXCLUDED.tipos,
                    video_stream = EXCLUDED.video_stream,
                    status       = 'online',
                    ultima_sync  = NOW()";
            cmd.Parameters.Add(new NpgsqlParameter { Value = sensorId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = gatewayId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = zona });
            cmd.Parameters.Add(new NpgsqlParameter { Value = tipos });
            cmd.Parameters.Add(new NpgsqlParameter { Value = videoStream });
            await cmd.ExecuteNonQueryAsync();
            RegistarLog($"Sensor reg.: {sensorId} | GW:{gatewayId} | ZONA:{zona} | VIDEO:{(videoStream ? "SIM" : "NAO")}");
        }
        catch (Exception ex)
        {
            RegistarLog($"Erro sensor UPSERT: {ex.Message}");
            return "{\"tipo\":\"ACK_SENSOR_REG\",\"status\":\"ERRO\"}";
        }

        return "{\"tipo\":\"ACK_SENSOR_REG\",\"status\":\"OK\"}";
    }

    static async Task<string> HandleSensorStatus(JsonElement root)
    {
        string sensorId = root.TryGetProperty("sensorId", out var s)  ? s.GetString() ?? "" : "";
        string estado   = root.TryGetProperty("estado",   out var e)  ? e.GetString() ?? "" : "";

        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE sensores SET status = $1, ultima_sync = NOW() WHERE sensor_id = $2";
            cmd.Parameters.Add(new NpgsqlParameter { Value = estado });
            cmd.Parameters.Add(new NpgsqlParameter { Value = sensorId });
            await cmd.ExecuteNonQueryAsync();
            RegistarLog($"Status: {sensorId} → {estado}");
        }
        catch (Exception ex)
        {
            RegistarLog($"Erro status UPDATE: {ex.Message}");
            return "{\"tipo\":\"ACK_STATUS\",\"status\":\"ERRO\"}";
        }

        return "{\"tipo\":\"ACK_STATUS\",\"status\":\"OK\"}";
    }

    #endregion

    #region BASE DE DADOS

    static void InicializarBaseDeDados()
    {
        try
        {
            using var conn = new NpgsqlConnection(connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS sensores (
                    sensor_id    VARCHAR(20) PRIMARY KEY,
                    gateway_id   VARCHAR(50) NOT NULL,
                    zona         VARCHAR(50) NOT NULL,
                    tipos        TEXT,
                    video_stream BOOLEAN     DEFAULT FALSE,
                    status       VARCHAR(20) DEFAULT 'desconhecido',
                    ultima_sync  TIMESTAMP,
                    registado_em TIMESTAMP   DEFAULT NOW()
                );
                CREATE TABLE IF NOT EXISTS leituras (
                    id         BIGSERIAL      PRIMARY KEY,
                    sensor_id  VARCHAR(20),
                    gateway_id VARCHAR(50),
                    zona       VARCHAR(50),
                    tipo_dado  VARCHAR(10),
                    valor      NUMERIC(10,3),
                    timestamp  TIMESTAMPTZ,
                    is_alarm   BOOLEAN        DEFAULT FALSE,
                    qualidade  REAL           DEFAULT 1.0
                );
                CREATE INDEX IF NOT EXISTS idx_leituras_zona_tipo_ts ON leituras (zona, tipo_dado, timestamp DESC);
                CREATE INDEX IF NOT EXISTS idx_leituras_sensor_ts    ON leituras (sensor_id, timestamp DESC);
                CREATE INDEX IF NOT EXISTS idx_leituras_alarmes      ON leituras (is_alarm) WHERE is_alarm = TRUE;";
            cmd.ExecuteNonQuery();
            RegistarLog("Schema verificado.");
        }
        catch (Exception ex) { RegistarLog($"Erro DB: {ex.Message}"); }
    }

    #endregion

    #region ENCERRAMENTO

    static void TratarEncerramento(object? sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        _isOnline = false;
        _server?.Stop();
        RegistarLog("Servidor encerrado.");
        Environment.Exit(0);
    }

    #endregion
}
