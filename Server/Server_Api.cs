using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Globalization;
using Grpc.Net.Client;
using ServicoAnalise;
using Npgsql;

// ==========================================
// REST API (porta 8080) + cliente gRPC para ServicoAnalise
// ==========================================
partial class ServerCentral
{
    private static readonly string _analiseUrl = Environment.GetEnvironmentVariable("ANALISE_URL") ?? "http://localhost:50052";
    private static AnaliseService.AnaliseServiceClient? _analiseClient;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    static void IniciarApi()
    {
        try
        {
            var canal = GrpcChannel.ForAddress(_analiseUrl);
            _analiseClient = new AnaliseService.AnaliseServiceClient(canal);
            RegistarLog($"[API] Canal gRPC para ServicoAnalise: {_analiseUrl}");
        }
        catch (Exception ex)
        {
            RegistarLog($"[API] Falha a ligar ao ServicoAnalise: {ex.Message}");
        }

        new Thread(ListenerApi) { IsBackground = true, Name = "REST-API" }.Start();
    }

    static void ListenerApi()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:8080/");

        try { listener.Start(); }
        catch (Exception ex)
        {
            RegistarLog($"[API] Falha a iniciar HttpListener na porta 8080: {ex.Message}");
            return;
        }

        RegistarLog("[API] REST API activa em http://localhost:8080/");

        while (_isOnline)
        {
            try
            {
                var ctx = listener.GetContext();
                _ = Task.Run(() => HandleRequestAsync(ctx));
            }
            catch (HttpListenerException) { break; }
            catch { }
        }

        listener.Stop();
    }

    static async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        // CORS headers para o frontend React
        res.AddHeader("Access-Control-Allow-Origin", "*");
        res.AddHeader("Access-Control-Allow-Methods", "GET, OPTIONS");
        res.AddHeader("Access-Control-Allow-Headers", "Content-Type");
        res.ContentType = "application/json; charset=utf-8";

        if (req.HttpMethod == "OPTIONS")
        {
            res.StatusCode = 204;
            res.Close();
            return;
        }

        try
        {
            string path = req.Url?.AbsolutePath.TrimEnd('/') ?? "/";
            var query   = req.QueryString;

            string json = path switch
            {
                "/api/sensores"      => await HandleSensores(),
                "/api/dados"         => await HandleDados(query),
                "/api/alarmes"       => await HandleAlarmes(query),
                "/api/analise"       => await HandleAnalise(query),
                "/api/padroes"       => await HandlePadroes(query),
                "/api/previsao"      => await HandlePrevisao(query),
                "/api/stream/start"  => await HandleStreamStart(query),
                "/api/stream/stop"   => HandleStreamStop(query),
                "/api/stream/estado" => HandleStreamEstado(),
                "/api/shutdown"      => HandleShutdown(),
                _                    => JsonSerializer.Serialize(new { erro = "Endpoint não encontrado." }, _jsonOpts)
            };

            byte[] buf = Encoding.UTF8.GetBytes(json);
            res.ContentLength64 = buf.Length;
            res.OutputStream.Write(buf, 0, buf.Length);
        }
        catch (Exception ex)
        {
            byte[] err = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { erro = ex.Message }, _jsonOpts));
            res.StatusCode = 500;
            res.OutputStream.Write(err, 0, err.Length);
        }
        finally { res.Close(); }
    }

    // GET /api/sensores — sensors from the sensores table + alarm count from leituras
    static async Task<string> HandleSensores()
    {
        var lista = new List<object>();
        using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.sensor_id, s.zona, s.tipos, s.video_stream, s.status, s.ultima_sync,
                   COUNT(l.id) FILTER (WHERE l.is_alarm) AS total_alarmes
            FROM sensores s
            LEFT JOIN leituras l ON l.sensor_id = s.sensor_id
            GROUP BY s.sensor_id, s.zona, s.tipos, s.video_stream, s.status, s.ultima_sync
            ORDER BY s.sensor_id";

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            string sId = reader.GetString(0);
            lista.Add(new
            {
                sensorId      = sId,
                zona          = reader.GetString(1),
                tipos         = reader.IsDBNull(2) ? "" : reader.GetString(2),
                videoStream   = reader.GetBoolean(3),
                status        = reader.IsDBNull(4) ? "desconhecido" : reader.GetString(4),
                ultimaLeitura = reader.IsDBNull(5) ? "" : reader.GetDateTime(5).ToString("yyyy-MM-dd HH:mm:ss"),
                totalAlarmes  = reader.IsDBNull(6) ? 0 : (int)reader.GetInt64(6)
            });
        }

        return JsonSerializer.Serialize(lista, _jsonOpts);
    }

    // GET /api/dados?zona=&tipo=&sensor=&inicio=&fim=&limite=100
    static async Task<string> HandleDados(System.Collections.Specialized.NameValueCollection q)
    {
        string zona   = q["zona"]   ?? "";
        string tipo   = q["tipo"]   ?? "";
        string sensor = q["sensor"] ?? "";
        string inicio = q["inicio"] ?? "";
        string fim    = q["fim"]    ?? "";
        int    limite = int.TryParse(q["limite"], out int l) ? Math.Min(l, 5000) : 200;

        var rows = new List<object>();
        using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        string sql = "SELECT gateway_id, sensor_id, zona, tipo_dado, valor, timestamp, is_alarm, qualidade FROM leituras WHERE 1=1";
        using var cmd = conn.CreateCommand();
        if (!string.IsNullOrEmpty(zona))   { sql += " AND zona=@zona";       cmd.Parameters.AddWithValue("@zona",   zona); }
        if (!string.IsNullOrEmpty(tipo))   { sql += " AND tipo_dado=@tipo";  cmd.Parameters.AddWithValue("@tipo",   tipo); }
        if (!string.IsNullOrEmpty(sensor)) { sql += " AND sensor_id=@sensor"; cmd.Parameters.AddWithValue("@sensor", sensor); }
        if (!string.IsNullOrEmpty(inicio) && DateTime.TryParse(inicio, out DateTime dtInicio))
            { sql += " AND timestamp>=@inicio"; cmd.Parameters.AddWithValue("@inicio", dtInicio); }
        if (!string.IsNullOrEmpty(fim) && DateTime.TryParse(fim, out DateTime dtFim))
            { sql += " AND timestamp<=@fim";    cmd.Parameters.AddWithValue("@fim",    dtFim); }

        sql += $" ORDER BY id DESC LIMIT {limite}";
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new
            {
                gatewayId = reader.IsDBNull(0) ? "" : reader.GetString(0),
                sensorId  = reader.IsDBNull(1) ? "" : reader.GetString(1),
                zona      = reader.IsDBNull(2) ? "" : reader.GetString(2),
                tipoDado  = reader.IsDBNull(3) ? "" : reader.GetString(3),
                valor     = reader.IsDBNull(4) ? "0" : reader.GetDecimal(4).ToString(CultureInfo.InvariantCulture),
                timestamp = reader.IsDBNull(5) ? "" : reader.GetDateTime(5).ToString("yyyy-MM-dd HH:mm:ss"),
                isAlarm   = !reader.IsDBNull(6) && reader.GetBoolean(6),
                qualidade = reader.IsDBNull(7) ? 1.0f : reader.GetFloat(7)
            });

        return JsonSerializer.Serialize(rows, _jsonOpts);
    }

    // GET /api/alarmes?zona=&tipo=&limite=50
    static async Task<string> HandleAlarmes(System.Collections.Specialized.NameValueCollection q)
    {
        string zona  = q["zona"] ?? "";
        string tipo  = q["tipo"] ?? "";
        int    limite = int.TryParse(q["limite"], out int l) ? Math.Min(l, 1000) : 50;

        var rows = new List<object>();
        using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        string sql = "SELECT gateway_id, sensor_id, zona, tipo_dado, valor, timestamp FROM leituras WHERE is_alarm = TRUE";
        using var cmd = conn.CreateCommand();
        if (!string.IsNullOrEmpty(zona)) { sql += " AND zona=@zona";      cmd.Parameters.AddWithValue("@zona", zona); }
        if (!string.IsNullOrEmpty(tipo)) { sql += " AND tipo_dado=@tipo"; cmd.Parameters.AddWithValue("@tipo", tipo); }

        sql += $" ORDER BY id DESC LIMIT {limite}";
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new
            {
                gatewayId = reader.IsDBNull(0) ? "" : reader.GetString(0),
                sensorId  = reader.IsDBNull(1) ? "" : reader.GetString(1),
                zona      = reader.IsDBNull(2) ? "" : reader.GetString(2),
                tipoDado  = reader.IsDBNull(3) ? "" : reader.GetString(3),
                valor     = reader.IsDBNull(4) ? "0" : reader.GetDecimal(4).ToString(CultureInfo.InvariantCulture),
                timestamp = reader.IsDBNull(5) ? "" : reader.GetDateTime(5).ToString("yyyy-MM-dd HH:mm:ss")
            });

        return JsonSerializer.Serialize(rows, _jsonOpts);
    }

    // GET /api/analise?zona=&tipo=&sensor=&inicio=&fim=
    static async Task<string> HandleAnalise(System.Collections.Specialized.NameValueCollection q)
    {
        if (_analiseClient == null)
            return JsonSerializer.Serialize(new { erro = "ServicoAnalise não disponível." }, _jsonOpts);

        var pedido = new PedidoAnalise
        {
            Zona       = q["zona"]   ?? "",
            TipoDado   = q["tipo"]   ?? "",
            SensorId   = q["sensor"] ?? "",
            DataInicio = q["inicio"] ?? "",
            DataFim    = q["fim"]    ?? ""
        };

        try
        {
            var r = await _analiseClient.AnalisarZonaAsync(pedido);
            return JsonSerializer.Serialize(new
            {
                zona          = r.Zona,
                tipoDado      = r.TipoDado,
                media         = r.Media,
                desvioPadrao  = r.DesvioPadrao,
                minimo        = r.Minimo,
                maximo        = r.Maximo,
                totalLeituras = r.TotalLeituras,
                totalAlarmes  = r.TotalAlarmes,
                timestamp     = r.Timestamp
            }, _jsonOpts);
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
        {
            return JsonSerializer.Serialize(new { erro = "Sem dados para os filtros fornecidos." }, _jsonOpts);
        }
        catch (Grpc.Core.RpcException ex)
        {
            return JsonSerializer.Serialize(new { erro = ex.Status.Detail }, _jsonOpts);
        }
    }

    // GET /api/padroes?zona=&tipo=&sensor=
    static async Task<string> HandlePadroes(System.Collections.Specialized.NameValueCollection q)
    {
        if (_analiseClient == null)
            return JsonSerializer.Serialize(new { erro = "ServicoAnalise não disponível." }, _jsonOpts);

        var pedido = new PedidoAnalise
        {
            Zona     = q["zona"]   ?? "",
            TipoDado = q["tipo"]   ?? "",
            SensorId = q["sensor"] ?? ""
        };

        try
        {
            var r = await _analiseClient.DetectarPadroesAsync(pedido);
            var padroes = new List<object>();
            foreach (var p in r.Padroes)
                padroes.Add(new { descricao = p.Descricao, confianca = p.Confianca, horaPico = p.HoraPico });

            return JsonSerializer.Serialize(new
            {
                zona      = r.Zona,
                tipoDado  = r.TipoDado,
                padroes,
                timestamp = r.Timestamp
            }, _jsonOpts);
        }
        catch (Grpc.Core.RpcException ex)
        {
            return JsonSerializer.Serialize(new { erro = ex.Status.Detail }, _jsonOpts);
        }
    }

    // GET /api/previsao?zona=&tipo=&horas=6
    static async Task<string> HandlePrevisao(System.Collections.Specialized.NameValueCollection q)
    {
        if (_analiseClient == null)
            return JsonSerializer.Serialize(new { erro = "ServicoAnalise não disponível." }, _jsonOpts);

        var pedido = new PedidoPrevisao
        {
            Zona         = q["zona"] ?? "",
            TipoDado     = q["tipo"] ?? "",
            HorasFuturas = int.TryParse(q["horas"], out int h) ? h : 6
        };

        try
        {
            var r = await _analiseClient.PreviRiscoAsync(pedido);
            return JsonSerializer.Serialize(new
            {
                zona              = r.Zona,
                tipoDado          = r.TipoDado,
                valoresPrevistos  = r.ValoresPrevistos,
                riscoSaude        = r.RiscoSaude,
                recomendacao      = r.Recomendacao,
                timestamp         = r.Timestamp
            }, _jsonOpts);
        }
        catch (Grpc.Core.RpcException ex)
        {
            return JsonSerializer.Serialize(new { erro = ex.Status.Detail }, _jsonOpts);
        }
    }

    // GET /api/shutdown
    static string HandleShutdown()
    {
        RegistarLog("[API] Shutdown solicitado pelo frontend.");
        Task.Delay(200).ContinueWith(_ =>
        {
            _isOnline = false;
            _server?.Stop();
            Environment.Exit(0);
        });
        return JsonSerializer.Serialize(new { ok = true }, _jsonOpts);
    }

    // GET /api/stream/start?sensor=
    static async Task<string> HandleStreamStart(System.Collections.Specialized.NameValueCollection q)
    {
        string sensorId = q["sensor"] ?? "";
        if (string.IsNullOrEmpty(sensorId))
            return JsonSerializer.Serialize(new { erro = "Parâmetro 'sensor' obrigatório." }, _jsonOpts);

        if (_streamingAtivo)
            return JsonSerializer.Serialize(new { erro = $"Stream já activo para {_streamingSensorId}." }, _jsonOpts);

        if (!_sensoresStream.TryGetValue(sensorId, out var info))
            return JsonSerializer.Serialize(new { erro = $"Sensor {sensorId} não registado como video-capable." }, _jsonOpts);

        if (!_gatewayIps.TryGetValue(info.GatewayId, out string gwIp))
            return JsonSerializer.Serialize(new { erro = $"IP do gateway {info.GatewayId} desconhecido." }, _jsonOpts);

        await IniciarStream(sensorId);
        if (!_streamingAtivo)
            return JsonSerializer.Serialize(new { erro = "Falha ao iniciar stream — ver logs do servidor." }, _jsonOpts);
        return JsonSerializer.Serialize(new { ok = true, sensor = sensorId, gateway = info.GatewayId, gwIp }, _jsonOpts);
    }

    // GET /api/stream/stop?sensor=
    static string HandleStreamStop(System.Collections.Specialized.NameValueCollection q)
    {
        PararStream();
        return JsonSerializer.Serialize(new { ok = true }, _jsonOpts);
    }

    // GET /api/stream/estado — debug: what does the server know about streams?
    static string HandleStreamEstado()
    {
        return JsonSerializer.Serialize(new
        {
            streamingAtivo    = _streamingAtivo,
            streamingSensorId = _streamingSensorId,
            sensoresStream    = _sensoresStream.Select(kv => new { sensorId = kv.Key, kv.Value.GatewayId, kv.Value.Zona }).ToList(),
            gatewayIps        = _gatewayIps.Select(kv => new { gatewayId = kv.Key, ip = kv.Value }).ToList()
        }, _jsonOpts);
    }
}
