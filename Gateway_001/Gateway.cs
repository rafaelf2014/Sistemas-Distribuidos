using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Grpc.Net.Client;
using PreProcessamento;
using ServicoAnalise;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

class TipoDadoConfig
{
    [JsonPropertyName("tipo")]           public string Tipo           { get; set; } = "";
    [JsonPropertyName("unidade")]        public string Unidade        { get; set; } = "";
    [JsonPropertyName("chunkSize")]      public int    ChunkSize      { get; set; } = 10;
    [JsonPropertyName("intervaloMaxMs")] public int    IntervaloMaxMs { get; set; } = 60000;
    [JsonPropertyName("modo")]           public string Modo           { get; set; } = "agregado";
}

class ConfigGateway
{
    [JsonPropertyName("gatewayId")]               public string               GatewayId               { get; set; } = "Gateway_001";
    [JsonPropertyName("serverIp")]                public string               ServerIp                { get; set; } = "127.0.0.1";
    [JsonPropertyName("rabbitMqHost")]            public string               RabbitMqHost            { get; set; } = "localhost";
    [JsonPropertyName("zonasSubscritas")]         public List<string>         ZonasSubscritas         { get; set; } = new();
    [JsonPropertyName("preProcessamentoGrpcUrl")] public string               PreProcessamentoGrpcUrl { get; set; } = "http://localhost:50051";
    [JsonPropertyName("analiseGrpcUrl")]          public string               AnaliseGrpcUrl          { get; set; } = "http://localhost:50052";
    [JsonPropertyName("tiposDados")]              public List<TipoDadoConfig> TiposDados              { get; set; } = new();
}

class SensorEntry
{
    [JsonPropertyName("id")]          public string Id          { get; set; } = "";
    [JsonPropertyName("status")]      public string Status      { get; set; } = "";
    [JsonPropertyName("zona")]        public string Zona        { get; set; } = "";
    [JsonPropertyName("tipos")]       public string Tipos       { get; set; } = "";
    [JsonPropertyName("videoStream")] public bool   VideoStream { get; set; }
    [JsonPropertyName("lastSync")]    public string LastSync    { get; set; } = "";
}

partial class MyTcpListener
{
    #region CAMPOS

    static string       _gatewayId       = "Gateway_001";
    static string       _serverIp        = "127.0.0.1";
    static string       _rabbitMqHost    = "localhost";
    static List<string> _zonasSubscritas = new();

    static IConnection? _amqpConnection;
    static IChannel?    _amqpChannel;

    static readonly string pastaProjeto     = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\"));
    static readonly string caminhoSensores  = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\sensores.json"));
    static readonly string caminhoAlarmes   = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\config_alarmes.json"));
    static readonly string caminhoPendentes = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\pendentes.json"));

    static readonly object fileLock            = new();
    static readonly object _alarmesLock        = new();
    static readonly object _alarmeCooldownLock = new();

    static readonly JsonSerializerOptions _jsonRead   = new() { PropertyNameCaseInsensitive = true };
    static readonly JsonSerializerOptions _jsonWrite  = new() { WriteIndented = false };
    static readonly JsonSerializerOptions _jsonPretty = new() { WriteIndented = true };

    static readonly Dictionary<string, (string Status, string Zona, string Tipos, bool VideoStream, DateTime LastSync)>
        _sensoresCache = new();

    static readonly Dictionary<string, DateTime>                     _ultimoAlarme  = new();
    static readonly Dictionary<string, string>                       _unidades       = new();
    static readonly ConcurrentDictionary<string, TipoDadoConfig>     _tipoConfigs    = new();
    static          Dictionary<string, Dictionary<string, double>>   _limitesAlarme  = new();

    // In-memory chunk buffer — key = "sensorId.TIPO"
    private record LeituraBuffer(string SensorId, string Zona, string TipoDado, string Unidade, double Valor, DateTime Timestamp, bool IsAlarm = false);

    // Fully enriched reading ready to send to server
    private record LeituraFinal(string SensorId, string Zona, string TipoDado, string Unidade,
                                 double Valor, DateTime Timestamp, float Qualidade,
                                 double? AnomalyScore, bool IsAlarm);
    static readonly ConcurrentDictionary<string, ConcurrentQueue<LeituraBuffer>> _buffer       = new();
    static readonly ConcurrentDictionary<string, DateTime>                        _bufferInicio = new();

    // Retry queue: JSON strings of batches that failed to reach the server
    const int MaxPendentes = 1000;
    static readonly ConcurrentQueue<string> _pendentesJson = new();

    // Persistent TCP connection to server
    static TcpClient?    _serverTcp;
    static StreamWriter? _serverWriter;
    static StreamReader? _serverReader;
    static readonly SemaphoreSlim _serverConnLock = new(1, 1);

    // Flush loop
    static CancellationTokenSource _cts       = new();
    static Task                    _flushTask = Task.CompletedTask;

    static System.Threading.Timer? _timerWatchdog;
    static System.Threading.Timer? _timerDashboard;

    static PreProcessamentoService.PreProcessamentoServiceClient? _grpcClient;
    static AnaliseService.AnaliseServiceClient?                   _analiseClient;

    #endregion

    #region INICIALIZAÇÃO

    public static async Task Main()
    {
        Console.CancelKeyPress += TratarEncerramento;
        InicializarSensoresJson();
        InicializarFicheiroAlarmesJson();
        InicializarGateway();
        CarregarPendentes();

        _timerWatchdog  = new System.Threading.Timer(_ => VerificarSensoresPerdidos(), null, 30000, 30000);
        _timerDashboard = new System.Threading.Timer(_ => { lock (_consoleLock) { DesenharDashboard(); } }, null, 250, 250);

        new Thread(IniciarListenerComandos) { IsBackground = true, Name = "CMD-Listener" }.Start();

        await InicializarRabbitMQ();
    }

    static async Task InicializarRabbitMQ()
    {
        const string EXCHANGE = "one_health";

        while (true)
        {
            try
            {
                RegistarLogEsquerda($"[AMQP] A ligar ao broker: {_rabbitMqHost}...");

                var factory = new ConnectionFactory { HostName = _rabbitMqHost };
                _amqpConnection = await factory.CreateConnectionAsync();
                _amqpChannel    = await _amqpConnection.CreateChannelAsync();

                await _amqpChannel.ExchangeDeclareAsync(EXCHANGE, ExchangeType.Topic, durable: true, autoDelete: false);

                foreach (string zona in _zonasSubscritas)
                {
                    string queue = $"gateway.{_gatewayId}.{zona}";
                    await _amqpChannel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
                    await _amqpChannel.QueueBindAsync(queue, EXCHANGE, $"{zona}.#");

                    var consumer = new AsyncEventingBasicConsumer(_amqpChannel);
                    consumer.ReceivedAsync += async (_, ea) =>
                    {
                        string msg = Encoding.UTF8.GetString(ea.Body.ToArray());
                        await Task.Run(() => ProcessarMensagemSensor(msg));
                        if (_amqpChannel != null)
                            await _amqpChannel.BasicAckAsync(ea.DeliveryTag, false);
                    };
                    await _amqpChannel.BasicConsumeAsync(queue, autoAck: false, consumer: consumer);
                    RegistarLogEsquerda($"[AMQP] Subscrito: {zona}");
                }

                _isOnline = true;
                RegistarLogEsquerda($"[AMQP] Broker ligado. A escutar {_zonasSubscritas.Count} zona(s).");

                while (_amqpConnection?.IsOpen ?? false)
                    await Task.Delay(2000);

                _isOnline = false;
                RegistarLogEsquerda("[AMQP] Ligação perdida. A reconectar...");
            }
            catch (Exception ex)
            {
                _isOnline = false;
                RegistarLogEsquerda($"[AMQP] Erro: {ex.Message}. A tentar em 5s...");
                await Task.Delay(5000);
            }
            finally
            {
                try { _amqpChannel?.Dispose(); _amqpConnection?.Dispose(); } catch { }
                _amqpChannel = null; _amqpConnection = null;
            }
        }
    }

    #endregion

    #region ALARMES

    static void InicializarFicheiroAlarmesJson()
    {
        lock (_alarmesLock)
        {
            if (!File.Exists(caminhoAlarmes))
            {
                _limitesAlarme = new();
                GuardarAlarmesJson();
            }
            else
            {
                try
                {
                    _limitesAlarme = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(
                        File.ReadAllText(caminhoAlarmes), _jsonRead) ?? new();
                }
                catch { _limitesAlarme = new(); }
            }
        }
    }

    static void GuardarAlarmesJson()
    {
        File.WriteAllText(caminhoAlarmes, JsonSerializer.Serialize(_limitesAlarme,
            _jsonPretty));
    }

    static void AutoPopularAlarmes(string zona, string tiposComBrackets)
    {
        string[] tipos = tiposComBrackets.Replace("[", "").Replace("]", "").Split(',');

        lock (_alarmesLock)
        {
            bool modificado = false;
            string z = zona.ToUpper();

            if (!_limitesAlarme.ContainsKey(z))
            {
                _limitesAlarme[z] = new();
                modificado = true;
                RegistarLogEsquerda($"AUTO-DISCOVERY: Nova Zona '{z}' detetada.");
            }

            foreach (string t in tipos)
            {
                string tipo = t.Trim().ToUpper();
                if (!string.IsNullOrEmpty(tipo) && !_limitesAlarme[z].ContainsKey(tipo))
                {
                    _limitesAlarme[z][tipo] = -1.0;
                    modificado = true;
                }
            }

            if (modificado) GuardarAlarmesJson();
        }
    }

    #endregion

    #region CONFIGURAÇÃO DO GATEWAY

    static void InicializarGateway()
    {
        string caminho = Path.Combine(pastaProjeto, "config_gateway.json");

        if (!File.Exists(caminho))
        {
            var def = new ConfigGateway
            {
                GatewayId = _gatewayId,
                TiposDados = new()
                {
                    new TipoDadoConfig { Tipo = "TEMP",  Unidade = "ºC",    ChunkSize = 10, IntervaloMaxMs = 60000 },
                    new TipoDadoConfig { Tipo = "HUM",   Unidade = "%",     ChunkSize = 10, IntervaloMaxMs = 60000 },
                    new TipoDadoConfig { Tipo = "CO2",   Unidade = "ppm",   ChunkSize = 10, IntervaloMaxMs = 60000 },
                    new TipoDadoConfig { Tipo = "RUIDO", Unidade = "dB",    ChunkSize = 10, IntervaloMaxMs = 60000 },
                    new TipoDadoConfig { Tipo = "LUMIN", Unidade = "lux",   ChunkSize = 10, IntervaloMaxMs = 60000 },
                    new TipoDadoConfig { Tipo = "PART",  Unidade = "µg/m³", ChunkSize = 10, IntervaloMaxMs = 60000 },
                    new TipoDadoConfig { Tipo = "NO2",   Unidade = "µg/m³", ChunkSize = 10, IntervaloMaxMs = 60000 },
                    new TipoDadoConfig { Tipo = "O3",    Unidade = "ppb",   ChunkSize = 10, IntervaloMaxMs = 60000 },
                    new TipoDadoConfig { Tipo = "WIND",  Unidade = "km/h",  ChunkSize = 10, IntervaloMaxMs = 60000 },
                }
            };
            File.WriteAllText(caminho, JsonSerializer.Serialize(def, _jsonPretty));
        }

        var cfg = JsonSerializer.Deserialize<ConfigGateway>(File.ReadAllText(caminho), _jsonRead)!;
        _gatewayId       = cfg.GatewayId;
        _serverIp        = cfg.ServerIp;
        _rabbitMqHost    = cfg.RabbitMqHost;
        _zonasSubscritas = cfg.ZonasSubscritas;

        foreach (var td in cfg.TiposDados)
        {
            string tipo = td.Tipo.ToUpper();
            _unidades[tipo]    = td.Unidade;
            _tipoConfigs[tipo] = td;
        }

        try
        {
            var canal = GrpcChannel.ForAddress(cfg.PreProcessamentoGrpcUrl);
            _grpcClient = new PreProcessamentoService.PreProcessamentoServiceClient(canal);
            RegistarLogEsquerda($"[gRPC] Canal PreProcessamento iniciado: {cfg.PreProcessamentoGrpcUrl}");
        }
        catch (Exception ex)
        {
            RegistarLogEsquerda($"[gRPC] Falha ao iniciar canal PreProcessamento: {ex.Message}");
        }

        try
        {
            var canalAnalise = GrpcChannel.ForAddress(cfg.AnaliseGrpcUrl);
            _analiseClient = new AnaliseService.AnaliseServiceClient(canalAnalise);
            RegistarLogEsquerda($"[gRPC] Canal Análise iniciado: {cfg.AnaliseGrpcUrl}");
        }
        catch (Exception ex)
        {
            RegistarLogEsquerda($"[gRPC] Falha ao iniciar canal Análise: {ex.Message}");
        }

        _flushTask = Task.Run(() => LoopFlush(_cts.Token));
        RegistarLogEsquerda("[Buffer] Flush loop iniciado.");
    }

    #endregion

    #region REGISTO DE SENSORES

    static void InicializarSensoresJson()
    {
        lock (fileLock)
        {
            if (!File.Exists(caminhoSensores)) { File.WriteAllText(caminhoSensores, "[]"); return; }

            try
            {
                var lista = JsonSerializer.Deserialize<List<SensorEntry>>(
                    File.ReadAllText(caminhoSensores), _jsonRead) ?? new();

                foreach (var s in lista)
                {
                    DateTime.TryParse(s.LastSync, out var lastSync);
                    _sensoresCache[s.Id] = (s.Status, s.Zona, s.Tipos, s.VideoStream, lastSync);
                }
            }
            catch { }
        }
    }

    static void PersistirCacheParaJson()
    {
        var lista = _sensoresCache.Select(kv => new SensorEntry
        {
            Id          = kv.Key,
            Status      = kv.Value.Status,
            Zona        = kv.Value.Zona,
            Tipos       = kv.Value.Tipos,
            VideoStream = kv.Value.VideoStream,
            LastSync    = kv.Value.LastSync.ToString("yyyy-MM-ddTHH:mm:ss")
        }).ToList();

        File.WriteAllText(caminhoSensores, JsonSerializer.Serialize(lista,
            _jsonPretty));
    }

    static void RegistarOuAtualizarSensor(string id, string zona, string tipos, bool videoStream)
    {
        lock (fileLock)
        {
            bool novo = !_sensoresCache.ContainsKey(id);
            _sensoresCache[id] = ("ativo", zona, tipos, videoStream, DateTime.Now);
            PersistirCacheParaJson();
            RegistarLogEsquerda(novo ? $"Config: Novo sensor {id} registado." : $"Config: Sensor {id} atualizado.");
        }
        _ = NotificarServidorStatus(id, "ativo");
    }

    static bool ValidarSensor(string id, string tipoDados)
    {
        if (string.IsNullOrEmpty(tipoDados)) return false;
        lock (fileLock)
        {
            if (!_sensoresCache.TryGetValue(id, out var s) || s.Status != "ativo") return false;
            var tipos = s.Tipos.Trim('[', ']').Split(',').Select(t => t.Trim());
            return tipos.Contains(tipoDados, StringComparer.OrdinalIgnoreCase);
        }
    }

    static void AtualizarLastSync(string id)
    {
        lock (fileLock)
        {
            if (_sensoresCache.TryGetValue(id, out var s))
                _sensoresCache[id] = (s.Status, s.Zona, s.Tipos, s.VideoStream, DateTime.Now);
        }
    }

    static void AtualizarEstadoSensor(string id, string estado)
    {
        lock (fileLock)
        {
            if (_sensoresCache.TryGetValue(id, out var s))
                _sensoresCache[id] = (estado, s.Zona, s.Tipos, s.VideoStream, s.LastSync);
            PersistirCacheParaJson();
            RegistarLogEsquerda($"Sensor {id} → {estado}.");
        }
        _ = NotificarServidorStatus(id, estado);
    }

    static string ObterZonaDoSensor(string id)
    {
        lock (fileLock)
        {
            return _sensoresCache.TryGetValue(id, out var s) ? s.Zona : "ZONA_DESCONHECIDA";
        }
    }

    static void VerificarSensoresPerdidos()
    {
        var perdidos = new List<string>();
        lock (fileLock)
        {
            bool alterado = false;
            foreach (var id in _sensoresCache.Keys.ToList())
            {
                var s = _sensoresCache[id];
                if (s.Status == "ativo" && (DateTime.Now - s.LastSync).TotalSeconds > 30)
                {
                    _sensoresCache[id] = ("manutencao", s.Zona, s.Tipos, s.VideoStream, s.LastSync);
                    alterado = true;
                    perdidos.Add(id);
                    RegistarLogEsquerda($"Watchdog: Sensor {id} sem heartbeat (Timeout).", true);
                }
            }
            if (alterado) PersistirCacheParaJson();
        }
        foreach (var id in perdidos)
            _ = NotificarServidorStatus(id, "manutencao");
    }

    #endregion

    #region BUFFER

    static async Task LoopFlush(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await VerificarFlushTodos();
        }
        catch (OperationCanceledException) { }
    }

    static void AdicionarAoBuffer(string sensorId, string zona, string tipoDado, double valor, DateTime timestamp, bool isAlarm = false)
    {
        string key   = $"{sensorId}.{tipoDado}";
        var    queue = _buffer.GetOrAdd(key, k =>
        {
            _bufferInicio[k] = DateTime.Now;
            return new ConcurrentQueue<LeituraBuffer>();
        });

        string unidade = _unidades.TryGetValue(tipoDado, out string? u) ? u : "";
        queue.Enqueue(new LeituraBuffer(sensorId, zona, tipoDado, unidade, valor, timestamp, isAlarm));

        // Immediate flush when chunk is full
        if (_tipoConfigs.TryGetValue(tipoDado, out var cfg) && queue.Count >= cfg.ChunkSize)
            _ = Task.Run(() => FlushBuffer(key));
    }

    static async Task VerificarFlushTodos()
    {
        // Drain retry queue (stop if server still unreachable)
        int maxRetry = _pendentesJson.Count;
        for (int i = 0; i < maxRetry; i++)
        {
            if (!_pendentesJson.TryDequeue(out string? json)) break;
            var resp = await EnviarMensagemAoServidor(json);
            if (resp == null) { _pendentesJson.Enqueue(json); break; }
            RegistarLogDireita("[RETRY] Batch reenviado", $"ACK: {resp.Trim()}");
        }

        // Time-based flush for slow sensors
        foreach (string key in _buffer.Keys.ToList())
        {
            int dot = key.IndexOf('.');
            if (dot < 0) continue;
            string tipo = key[(dot + 1)..];

            if (!_tipoConfigs.TryGetValue(tipo, out var cfg)) continue;
            if (!_bufferInicio.TryGetValue(key, out DateTime inicio)) continue;
            if ((DateTime.Now - inicio).TotalMilliseconds >= cfg.IntervaloMaxMs)
                await FlushBuffer(key);
        }
    }

    static async Task FlushBuffer(string key)
    {
        if (!_buffer.TryRemove(key, out var queue)) return;
        _bufferInicio.TryRemove(key, out _);

        LeituraBuffer[] amostras = queue.ToArray();
        if (amostras.Length == 0) return;

        var validas = new List<(LeituraBuffer Original, double ValorFinal, float Qualidade)>();

        if (_grpcClient != null)
        {
            try
            {
                var bloco = new BlocoLeiturasBrutas();
                foreach (var a in amostras)
                    bloco.Dados.Add(new LeituraBruta
                    {
                        GatewayId = _gatewayId,
                        SensorId  = a.SensorId,
                        Zona      = a.Zona,
                        Tipo      = a.TipoDado,
                        Valor     = a.Valor,
                        Unidade   = a.Unidade,
                        Timestamp = a.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss")
                    });

                var resultado = await _grpcClient.NormalizarBlocoAsync(bloco);

                for (int i = 0; i < Math.Min(resultado.Dados.Count, amostras.Length); i++)
                {
                    var r = resultado.Dados[i];
                    if (r.Valido) validas.Add((amostras[i], r.ValorNormalizado, r.Qualidade));
                    else          RegistarLogEsquerda($"[PreProc] Rejeitado: {r.Observacao}");
                }
            }
            catch (Exception ex)
            {
                RegistarLogEsquerda($"[gRPC] Falha: {ex.Message}. A usar valores brutos.");
                validas.AddRange(amostras.Select(a => (a, a.Valor, 0.5f)));
            }
        }
        else
        {
            validas.AddRange(amostras.Select(a => (a, a.Valor, 1.0f)));
        }

        // Convert to final enriched records
        var finais = validas.Select(v => new LeituraFinal(
            v.Original.SensorId, v.Original.Zona, v.Original.TipoDado, v.Original.Unidade,
            v.ValorFinal, v.Original.Timestamp, v.Qualidade,
            AnomalyScore: null, IsAlarm: v.Original.IsAlarm
        )).ToList();

        // ML anomaly scoring
        if (_analiseClient != null && finais.Count > 0)
        {
            try
            {
                var pedido = new PedidoScoreBatch { GatewayId = _gatewayId };
                foreach (var f in finais)
                    pedido.Leituras.Add(new LeituraScore
                    {
                        SensorId  = f.SensorId,
                        Zona      = f.Zona,
                        Tipo      = f.TipoDado,
                        Valor     = f.Valor,
                        Timestamp = f.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss")
                    });

                var resultado = await _analiseClient.ScoreBatchAsync(pedido);

                if (resultado.ModeloAquecido)
                {
                    for (int i = 0; i < Math.Min(resultado.Anomalias.Count, finais.Count); i++)
                    {
                        var a = resultado.Anomalias[i];
                        finais[i] = finais[i] with { AnomalyScore = a.Score };
                        if (a.IsAnomalia)
                            RegistarLogEsquerda(
                                $"[ML] Anomalia: {finais[i].SensorId} {finais[i].TipoDado} score={a.Score:F2} — {a.Motivo}", true);
                    }
                }
            }
            catch (Exception ex) { RegistarLogEsquerda($"[ML] ScoreBatch falhou: {ex.Message}"); }
        }

        if (finais.Count > 0)
            await EnviarBatchParaServidor(finais);
    }

    #endregion

    #region HANDLER DE SENSORES

    static void ProcessarMensagemSensor(string rawData)
    {
        try
        {
            string[] parts  = rawData.Split('|');
            string   command = parts[0].ToUpper();

            switch (command)
            {
                case "HELLO":
                    if (parts.Length >= 4)
                    {
                        bool videoCapable = parts.Length >= 5 &&
                                            bool.TryParse(parts[4], out bool vc) && vc;
                        RegistarOuAtualizarSensor(parts[1], parts[2], parts[3], videoCapable);
                        AutoPopularAlarmes(parts[2], parts[3]);
                        _ = EnviarRegistoSensorParaServidor(parts[1], parts[2], parts[3], videoCapable);
                    }
                    break;

                case "DATA_SEND":
                    if (parts.Length >= 5 &&
                        double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out double valor))
                    {
                        string sensorId = parts[1];
                        string tipoDado = parts[2].ToUpper();

                        if (!ValidarSensor(sensorId, tipoDado))
                        {
                            RegistarLogEsquerda($"[WARN] {sensorId}: dados rejeitados — sensor não registado ou tipo '{tipoDado}' inválido.");
                            break;
                        }

                        DateTime timestamp = DateTime.TryParse(parts[4], null,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out DateTime ts) ? ts : DateTime.UtcNow;
                        string   zona      = ObterZonaDoSensor(sensorId).ToUpper();
                        string   un        = _unidades.TryGetValue(tipoDado, out string? u) ? u : "";

                        // Edge alarm check — immediate, no network required
                        bool isAnomalia = false;
                        lock (_alarmesLock)
                        {
                            if (_limitesAlarme.TryGetValue(zona, out var z) &&
                                z.TryGetValue(tipoDado, out double limite) &&
                                limite != -1.0 && valor > limite)
                                isAnomalia = true;
                        }

                        bool alarmeAtivo = false;
                        if (isAnomalia)
                        {
                            string chave = $"{sensorId}.{tipoDado}";
                            bool emCooldown;
                            lock (_alarmeCooldownLock)
                            {
                                emCooldown = _ultimoAlarme.TryGetValue(chave, out DateTime ultimo) &&
                                             (DateTime.Now - ultimo).TotalSeconds < 30;
                                if (!emCooldown) _ultimoAlarme[chave] = DateTime.Now;
                            }

                            if (!emCooldown)
                            {
                                alarmeAtivo = true;
                                RegistarLogEsquerda(
                                    $"EDGE ANALYTICS: Anomalia em {sensorId}! ({tipoDado} = {valor}{un} @ {zona})", true);
                            }
                        }

                        AdicionarAoBuffer(sensorId, zona, tipoDado, valor, timestamp, alarmeAtivo);
                        RegistarLogEsquerda($"{sensorId}: {tipoDado} = {valor}{un}");
                    }
                    break;

                case "HEARTBEAT":
                    if (parts.Length >= 2) AtualizarLastSync(parts[1]);
                    break;

                case "BYE":
                    if (parts.Length >= 2) AtualizarEstadoSensor(parts[1], "desativado");
                    break;
            }
        }
        catch (Exception ex) { RegistarLogEsquerda($"Erro ao processar mensagem: {ex.Message}"); }
    }

    #endregion

    #region COMUNICAÇÃO COM SERVIDOR

    static async Task<string?> EnviarMensagemAoServidor(string json)
    {
        await _serverConnLock.WaitAsync();
        try
        {
            for (int tentativa = 0; tentativa < 2; tentativa++)
            {
                try
                {
                    if (_serverTcp == null || !_serverTcp.Connected)
                        await ReconectarAoServidor();

                    await _serverWriter!.WriteLineAsync(json);
                    return await _serverReader!.ReadLineAsync();
                }
                catch
                {
                    try { _serverTcp?.Close(); } catch { }
                    _serverTcp = null;
                }
            }
            return null;
        }
        finally { _serverConnLock.Release(); }
    }

    static async Task ReconectarAoServidor()
    {
        _serverTcp = new TcpClient();
        await _serverTcp.ConnectAsync(_serverIp, 14000);
        var stream = _serverTcp.GetStream();
        _serverWriter = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        _serverReader = new StreamReader(stream, Encoding.UTF8);
        RegistarLogEsquerda($"[TCP] Ligado ao servidor {_serverIp}:14000.");
    }

    static async Task EnviarBatchParaServidor(List<LeituraFinal> leituras)
    {
        string json = JsonSerializer.Serialize(new
        {
            tipo      = "DATA_BATCH",
            gatewayId = _gatewayId,
            leituras  = leituras.Select(l => new
            {
                sensorId     = l.SensorId,
                zona         = l.Zona,
                tipoDado     = l.TipoDado,
                valor        = l.Valor,
                timestamp    = l.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss"),
                qualidade    = l.Qualidade,
                isAlarm      = l.IsAlarm,
                anomalyScore = l.AnomalyScore
            }).ToArray()
        }, _jsonWrite);

        var resposta = await EnviarMensagemAoServidor(json);

        if (resposta == null || !resposta.Contains("OK"))
        {
            if (_pendentesJson.Count >= MaxPendentes)
            {
                _pendentesJson.TryDequeue(out _);
                RegistarLogEsquerda("[TCP] Fila cheia — batch mais antigo descartado.", true);
            }
            _pendentesJson.Enqueue(json);
            RegistarLogEsquerda($"[TCP] Batch retido ({leituras.Count} leituras).", true);
        }
        else
        {
            string tipo = leituras.First().TipoDado;
            RegistarLogDireita($"BATCH {leituras.Count}x ({tipo})", $"ACK: {resposta.Trim()}");
        }
    }

    static async Task EnviarRegistoSensorParaServidor(string sensorId, string zona, string tipos, bool videoCapable)
    {
        string json = JsonSerializer.Serialize(new
        {
            tipo        = "SENSOR_REG",
            gatewayId   = _gatewayId,
            sensorId, zona, tipos,
            videoStream = videoCapable
        }, _jsonWrite);

        await EnviarMensagemAoServidor(json);
    }

    static async Task NotificarServidorStatus(string sensorId, string estado)
    {
        string json = JsonSerializer.Serialize(new
        {
            tipo      = "SENSOR_STATUS",
            gatewayId = _gatewayId,
            sensorId, estado
        }, _jsonWrite);

        await EnviarMensagemAoServidor(json);
    }

    static void CarregarPendentes()
    {
        if (!File.Exists(caminhoPendentes)) return;
        try
        {
            var lista = JsonSerializer.Deserialize<string[]>(File.ReadAllText(caminhoPendentes)) ?? [];
            foreach (var json in lista) _pendentesJson.Enqueue(json);
            RegistarLogEsquerda($"[Buffer] {lista.Length} batch(es) pendentes recarregados.");
        }
        catch { }
    }

    static void SalvarPendentes()
    {
        var lista = _pendentesJson.ToArray();
        if (lista.Length > 0)
            File.WriteAllText(caminhoPendentes,
                JsonSerializer.Serialize(lista, _jsonPretty));
        else if (File.Exists(caminhoPendentes))
            File.Delete(caminhoPendentes);
    }

    #endregion

    #region ENCERRAMENTO

    static void TratarEncerramento(object? sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        _isOnline = false;
        RegistarLogEsquerda("A encerrar o Gateway. A aguardar flush final...");

        _cts.Cancel();
        _flushTask.Wait(TimeSpan.FromSeconds(5));

        _timerWatchdog?.Dispose();
        _timerDashboard?.Dispose();
        lock (_alarmesLock) { GuardarAlarmesJson(); }
        SalvarPendentes();

        try { _amqpChannel?.Dispose(); _amqpConnection?.Dispose(); } catch { }
        try { _serverTcp?.Close(); } catch { }

        Environment.Exit(0);
    }

    #endregion
}
