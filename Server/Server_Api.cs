using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Globalization;
using Grpc.Net.Client;
using ServicoAnalise;
using Microsoft.Data.Sqlite;

// ==========================================
// REST API (porta 8080) + cliente gRPC para ServicoAnalise
// ==========================================
partial class ServerCentral
{
    private static readonly string _analiseUrl = "http://localhost:50052";
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
                ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
            }
            catch (HttpListenerException) { break; }
            catch { }
        }

        listener.Stop();
    }

    static void HandleRequest(HttpListenerContext ctx)
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
                "/api/sensores"  => HandleSensores(),
                "/api/dados"     => HandleDados(query),
                "/api/alarmes"   => HandleAlarmes(query),
                "/api/analise"   => HandleAnalise(query).GetAwaiter().GetResult(),
                "/api/padroes"   => HandlePadroes(query).GetAwaiter().GetResult(),
                "/api/previsao"  => HandlePrevisao(query).GetAwaiter().GetResult(),
                _                => JsonSerializer.Serialize(new { erro = "Endpoint não encontrado." }, _jsonOpts)
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

    // GET /api/sensores — distinct sensors seen in the DB + stream-capable list
    static string HandleSensores()
    {
        var lista = new List<object>();
        using var conn = new SqliteConnection(connectionString);
        conn.Open();

        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT SensorId, Zona, GROUP_CONCAT(DISTINCT TipoDado) AS Tipos,
                   MAX(Timestamp) AS UltimaLeitura,
                   SUM(CASE WHEN IsAlarm=1 THEN 1 ELSE 0 END) AS TotalAlarmes
            FROM Dados
            GROUP BY SensorId, Zona
            ORDER BY SensorId";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string sId    = reader.GetString(0);
            string status = _sensoresStatus.TryGetValue(sId, out var st) ? st : "desconhecido";
            lista.Add(new
            {
                sensorId      = sId,
                zona          = reader.GetString(1),
                tipos         = reader.GetString(2),
                ultimaLeitura = reader.IsDBNull(3) ? "" : reader.GetString(3),
                totalAlarmes  = reader.GetInt32(4),
                videoStream   = _sensoresStream.ContainsKey(sId),
                status
            });
        }

        return JsonSerializer.Serialize(lista, _jsonOpts);
    }

    // GET /api/dados?zona=&tipo=&sensor=&inicio=&fim=&limite=100
    static string HandleDados(System.Collections.Specialized.NameValueCollection q)
    {
        string zona    = q["zona"]   ?? "";
        string tipo    = q["tipo"]   ?? "";
        string sensor  = q["sensor"] ?? "";
        string inicio  = q["inicio"] ?? "";
        string fim     = q["fim"]    ?? "";
        int    limite  = int.TryParse(q["limite"], out int l) ? Math.Min(l, 5000) : 200;

        var rows = new List<object>();
        using var conn = new SqliteConnection(connectionString);
        conn.Open();

        string sql = "SELECT GatewayId, SensorId, Zona, TipoDado, Valor, Timestamp, IsAlarm FROM Dados WHERE 1=1";
        var cmd = conn.CreateCommand();
        if (!string.IsNullOrEmpty(zona))   { sql += " AND Zona=@zona";   cmd.Parameters.AddWithValue("@zona",   zona); }
        if (!string.IsNullOrEmpty(tipo))   { sql += " AND TipoDado=@tipo"; cmd.Parameters.AddWithValue("@tipo", tipo); }
        if (!string.IsNullOrEmpty(sensor)) { sql += " AND SensorId=@sensor"; cmd.Parameters.AddWithValue("@sensor", sensor); }
        if (!string.IsNullOrEmpty(inicio)) { sql += " AND Timestamp>=@inicio"; cmd.Parameters.AddWithValue("@inicio", inicio); }
        if (!string.IsNullOrEmpty(fim))    { sql += " AND Timestamp<=@fim";    cmd.Parameters.AddWithValue("@fim",    fim); }

        sql += $" ORDER BY Id DESC LIMIT {limite}";
        cmd.CommandText = sql;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new
            {
                gatewayId = reader.GetString(0),
                sensorId  = reader.GetString(1),
                zona      = reader.GetString(2),
                tipoDado  = reader.GetString(3),
                valor     = reader.GetString(4),
                timestamp = reader.GetString(5),
                isAlarm   = reader.GetInt32(6) == 1
            });

        return JsonSerializer.Serialize(rows, _jsonOpts);
    }

    // GET /api/alarmes?zona=&tipo=&limite=50
    static string HandleAlarmes(System.Collections.Specialized.NameValueCollection q)
    {
        string zona  = q["zona"] ?? "";
        string tipo  = q["tipo"] ?? "";
        int    limite = int.TryParse(q["limite"], out int l) ? Math.Min(l, 1000) : 50;

        var rows = new List<object>();
        using var conn = new SqliteConnection(connectionString);
        conn.Open();

        string sql = "SELECT GatewayId, SensorId, Zona, TipoDado, Valor, Timestamp FROM Dados WHERE IsAlarm=1";
        var cmd = conn.CreateCommand();
        if (!string.IsNullOrEmpty(zona)) { sql += " AND Zona=@zona";   cmd.Parameters.AddWithValue("@zona", zona); }
        if (!string.IsNullOrEmpty(tipo)) { sql += " AND TipoDado=@tipo"; cmd.Parameters.AddWithValue("@tipo", tipo); }

        sql += $" ORDER BY Id DESC LIMIT {limite}";
        cmd.CommandText = sql;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new
            {
                gatewayId = reader.GetString(0),
                sensorId  = reader.GetString(1),
                zona      = reader.GetString(2),
                tipoDado  = reader.GetString(3),
                valor     = reader.GetString(4),
                timestamp = reader.GetString(5)
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
}
