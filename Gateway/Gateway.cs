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

// Configuracao de um tipo de dado: unidade, tamanho do lote e tempo maximo de buffer.
class TipoDadoConfig
{
    [JsonPropertyName("tipo")]           public string Tipo           { get; set; } = "";
    [JsonPropertyName("unidade")]        public string Unidade        { get; set; } = "";
    [JsonPropertyName("chunkSize")]      public int    ChunkSize      { get; set; } = 10;
    [JsonPropertyName("intervaloMaxMs")] public int    IntervaloMaxMs { get; set; } = 60000;
    [JsonPropertyName("modo")]           public string Modo           { get; set; } = "agregado";
}

// Configuracao do gateway lida de config_gateway.json. comandoPort e a porta TCP onde
// o gateway escuta pedidos de video do servidor (diferente por gateway na mesma maquina).
class ConfigGateway
{
    [JsonPropertyName("gatewayId")]               public string               GatewayId               { get; set; } = "Gateway_001";
    [JsonPropertyName("serverIp")]                public string               ServerIp                { get; set; } = "127.0.0.1";
    [JsonPropertyName("rabbitMqHost")]            public string               RabbitMqHost            { get; set; } = "localhost";
    [JsonPropertyName("zonasSubscritas")]         public List<string>         ZonasSubscritas         { get; set; } = new();
    [JsonPropertyName("preProcessamentoGrpcUrl")] public string               PreProcessamentoGrpcUrl { get; set; } = "http://localhost:50051";
    [JsonPropertyName("analiseGrpcUrl")]          public string               AnaliseGrpcUrl          { get; set; } = "http://localhost:50052";
    [JsonPropertyName("comandoPort")]             public int                  ComanodoPort            { get; set; } = 14001;
    [JsonPropertyName("tiposDados")]              public List<TipoDadoConfig> TiposDados              { get; set; } = new();
}

// Registo persistido de um sensor conhecido (espelha o ficheiro sensores.json).
class SensorEntry
{
    [JsonPropertyName("id")]          public string Id          { get; set; } = "";
    [JsonPropertyName("status")]      public string Status      { get; set; } = "";
    [JsonPropertyName("zona")]        public string Zona        { get; set; } = "";
    [JsonPropertyName("tipos")]       public string Tipos       { get; set; } = "";
    [JsonPropertyName("videoStream")] public bool   VideoStream { get; set; }
    [JsonPropertyName("lastSync")]    public string LastSync    { get; set; } = "";
}

partial class Gateway
{
    #region CAMPOS

    // Identidade e ligacoes do gateway, preenchidas a partir da configuracao.
    string       _gatewayId       = "Gateway_001";
    string       _serverIp        = "127.0.0.1";
    string       _rabbitMqHost    = "localhost";
    int          _comandoPort     = 14001;
    List<string> _zonasSubscritas = new();

    IConnection? _amqpConnection;
    IChannel?    _amqpChannel;

    // Caminhos dos ficheiros de estado, todos dentro do diretorio de configuracao.
    readonly string pastaProjeto;
    readonly string caminhoSensores;
    readonly string caminhoAlarmes;
    readonly string caminhoPendentes;

    readonly object fileLock            = new();
    readonly object _alarmesLock        = new();
    readonly object _alarmeCooldownLock = new();

    readonly JsonSerializerOptions _jsonRead   = new() { PropertyNameCaseInsensitive = true };
    readonly JsonSerializerOptions _jsonWrite  = new() { WriteIndented = false };
    readonly JsonSerializerOptions _jsonPretty = new() { WriteIndented = true };

    // Cache em memoria do estado de cada sensor (persistida em sensores.json).
    readonly Dictionary<string, (string Status, string Zona, string Tipos, bool VideoStream, DateTime LastSync)>
        _sensoresCache = new();

    readonly Dictionary<string, DateTime>                     _ultimoAlarme  = new(); // cooldown de alarme por sensor.tipo
    readonly Dictionary<string, string>                       _unidades       = new(); // unidade padrao por tipo
    readonly ConcurrentDictionary<string, TipoDadoConfig>     _tipoConfigs    = new(); // config por tipo
             Dictionary<string, Dictionary<string, double>>   _limitesAlarme  = new(); // limiar por zona e tipo

    // Uma leitura em espera no buffer.
    private record LeituraBuffer(string SensorId, string Zona, string TipoDado, string Unidade, double Valor, DateTime Timestamp);

    // Leitura ja normalizada e pronta a enviar para o servidor.
    private record LeituraFinal(string SensorId, string Zona, string TipoDado, string Unidade,
                                 double Valor, DateTime Timestamp, float Qualidade,
                                 double? AnomalyScore, bool IsAlarm);

    // Buffer por chave "sensorId.TIPO". Guarda a fila de leituras e o instante de inicio,
    // num so registo, para que remover ambos seja atomico.
    private record BufferEntry(ConcurrentQueue<LeituraBuffer> Queue, DateTime Inicio);
    readonly ConcurrentDictionary<string, BufferEntry> _buffer = new();

    // Fila de reenvio: lotes em JSON que falharam a chegar ao servidor (persistida).
    const int MaxPendentes  = 1000;
    const int MaxRawBuffer  = 500;
    readonly ConcurrentQueue<string>                   _pendentesJson = new();

    // Mensagens em formato nao-pipe (json/xml/querystring/hex) a espera de normalizacao.
    readonly ConcurrentQueue<(string Zona, string RawPayload)> _rawBuffer = new();

    // Ligacao TCP persistente ao servidor central.
    TcpClient?    _serverTcp;
    StreamWriter? _serverWriter;
    StreamReader? _serverReader;
    readonly SemaphoreSlim _serverConnLock = new(1, 1);

    // Ciclo periodico de flush do buffer.
    CancellationTokenSource _cts       = new();
    Task                    _flushTask = Task.CompletedTask;

    System.Threading.Timer? _timerWatchdog;
    System.Threading.Timer? _timerDashboard;

    // Clientes gRPC: normalizacao e analise/scoring.
    PreProcessamentoService.PreProcessamentoServiceClient? _grpcClient;
    AnaliseService.AnaliseServiceClient?                   _analiseClient;

    #endregion

    #region INICIALIZACAO

    public Gateway(string configDir)
    {
        pastaProjeto     = Path.GetFullPath(configDir);
        caminhoSensores  = Path.Combine(pastaProjeto, "sensores.json");
        caminhoAlarmes   = Path.Combine(pastaProjeto, "config_alarmes.json");
        caminhoPendentes = Path.Combine(pastaProjeto, "pendentes.json");
    }

    // Ponto de entrada. Le o diretorio de configuracao do argumento.
    public static async Task Main(string[] args)
    {
        string configDir = args.Length > 0
            ? args[0]
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\"));
        await new Gateway(configDir).RunAsync();
    }

    // Arranque: carrega estado, inicia o watchdog, o dashboard e o listener de comandos,
    // e entra no ciclo de ligacao ao broker.
    public async Task RunAsync()
    {
        Console.CancelKeyPress += TratarEncerramento;
        InicializarSensoresJson();
        InicializarFicheiroAlarmesJson();
        InicializarGateway();
        CarregarPendentes();

        // Watchdog marca sensores sem heartbeat; dashboard redesenha periodicamente.
        _timerWatchdog  = new System.Threading.Timer(_ => VerificarSensoresPerdidos(), null, 30000, 30000);
        _timerDashboard = new System.Threading.Timer(_ => { lock (_consoleLock) { DesenharDashboard(); } }, null, 250, 250);

        new Thread(IniciarListenerComandos) { IsBackground = true, Name = "CMD-Listener" }.Start();

        await InicializarRabbitMQ();
    }

    // Liga ao broker, cria uma fila por zona subscrita e consome as mensagens.
    // Reconecta automaticamente se a ligacao cair.
    async Task InicializarRabbitMQ()
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
                    // Liga a fila ao exchange com o padrao {zona}.# (todas as mensagens da zona).
                    string queue = $"gateway.{_gatewayId}.{zona}";
                    await _amqpChannel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
                    await _amqpChannel.QueueBindAsync(queue, EXCHANGE, $"{zona}.#");

                    var consumer = new AsyncEventingBasicConsumer(_amqpChannel);
                    consumer.ReceivedAsync += async (_, ea) =>
                    {
                        string msg          = Encoding.UTF8.GetString(ea.Body.ToArray());
                        string zonaRoteamento = ea.RoutingKey.Split('.')[0];
                        await Task.Run(() => ProcessarMensagemSensor(msg, zonaRoteamento));
                        if (_amqpChannel != null)
                            await _amqpChannel.BasicAckAsync(ea.DeliveryTag, false);
                    };
                    await _amqpChannel.BasicConsumeAsync(queue, autoAck: false, consumer: consumer);
                    RegistarLogEsquerda($"Subscrito: {zona}");
                }

                _isOnline = true;
                RegistarLogEsquerda($"Broker ligado. A escutar {_zonasSubscritas.Count} zona(s).");

                while (_amqpConnection?.IsOpen ?? false)
                    await Task.Delay(2000);

                _isOnline = false;
                RegistarLogEsquerda("Ligacao perdida. A reconectar...");
            }
            catch (Exception ex)
            {
                _isOnline = false;
                RegistarLogEsquerda($"Erro: {ex.Message}. A tentar em 5s...");
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

    // Carrega os limiares de alarme do ficheiro, ou cria um vazio se nao existir.
    void InicializarFicheiroAlarmesJson()
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

    void GuardarAlarmesJson()
    {
        File.WriteAllText(caminhoAlarmes, JsonSerializer.Serialize(_limitesAlarme,
            _jsonPretty));
    }

    // Quando um sensor novo se anuncia, garante que existem entradas de alarme para a sua
    // zona e tipos (com limiar -1, ou seja, desativado por omissao).
    void AutoPopularAlarmes(string zona, string tiposComBrackets)
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

    #region CONFIGURACAO DO GATEWAY

    // Le config_gateway.json (cria um por omissao se faltar), prepara as unidades por tipo,
    // abre os canais gRPC e arranca o ciclo de flush.
    void InicializarGateway()
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
        // Variaveis de ambiente sobrepoem-se a config (deploy multi-PC).
        _serverIp        = Environment.GetEnvironmentVariable("SERVER_IP")     ?? cfg.ServerIp;
        _rabbitMqHost    = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? cfg.RabbitMqHost;
        _comandoPort     = cfg.ComanodoPort;
        _zonasSubscritas = cfg.ZonasSubscritas;

        string preProc = Environment.GetEnvironmentVariable("PREPROCESSAMENTO_GRPC_URL") ?? cfg.PreProcessamentoGrpcUrl;
        string analise = Environment.GetEnvironmentVariable("ANALISE_GRPC_URL")           ?? cfg.AnaliseGrpcUrl;

        foreach (var td in cfg.TiposDados)
        {
            string tipo = td.Tipo.ToUpper();
            _unidades[tipo]    = td.Unidade;
            _tipoConfigs[tipo] = td;
        }

        try
        {
            var canal = GrpcChannel.ForAddress(preProc);
            _grpcClient = new PreProcessamentoService.PreProcessamentoServiceClient(canal);
            RegistarLogEsquerda($"[gRPC] Canal PreProcessamento iniciado: {preProc}");
        }
        catch (Exception ex)
        {
            RegistarLogEsquerda($"[gRPC] Falha ao iniciar canal PreProcessamento: {ex.Message}");
        }

        try
        {
            var canalAnalise = GrpcChannel.ForAddress(analise);
            _analiseClient = new AnaliseService.AnaliseServiceClient(canalAnalise);
            RegistarLogEsquerda($"[gRPC] Canal Analise iniciado: {analise}");
        }
        catch (Exception ex)
        {
            RegistarLogEsquerda($"[gRPC] Falha ao iniciar canal Analise: {ex.Message}");
        }

        _flushTask = Task.Run(() => LoopFlush(_cts.Token));
        RegistarLogEsquerda("[Buffer] Flush loop iniciado.");
    }

    #endregion

    #region REGISTO DE SENSORES

    // Carrega a cache de sensores a partir do ficheiro no arranque.
    void InicializarSensoresJson()
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

    // Escreve toda a cache de sensores no ficheiro.
    void PersistirCacheParaJson()
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

    // Regista ou atualiza um sensor (ao receber HELLO) e notifica o servidor.
    void RegistarOuAtualizarSensor(string id, string zona, string tipos, bool videoStream)
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

    // Verifica se um sensor esta registado, ativo e produz o tipo de dado indicado.
    bool ValidarSensor(string id, string tipoDados)
    {
        if (string.IsNullOrEmpty(tipoDados)) return false;
        lock (fileLock)
        {
            if (!_sensoresCache.TryGetValue(id, out var s) || s.Status != "ativo") return false;
            var tipos = s.Tipos.Trim('[', ']').Split(',').Select(t => t.Trim());
            return tipos.Contains(tipoDados, StringComparer.OrdinalIgnoreCase);
        }
    }

    // Atualiza o instante do ultimo contacto. Se o sensor estava em manutencao, recupera-o
    // para ativo (um heartbeat depois de um timeout volta a por o sensor online).
    void AtualizarLastSync(string id)
    {
        bool promovido = false;
        lock (fileLock)
        {
            if (_sensoresCache.TryGetValue(id, out var s))
            {
                string novoStatus = s.Status == "manutencao" ? "ativo" : s.Status;
                _sensoresCache[id] = (novoStatus, s.Zona, s.Tipos, s.VideoStream, DateTime.Now);
                if (novoStatus != s.Status) { PersistirCacheParaJson(); promovido = true; }
            }
        }
        if (promovido)
        {
            RegistarLogEsquerda($"[HEARTBEAT] {id} recuperado: manutencao -> ativo.");
            _ = NotificarServidorStatus(id, "ativo");
        }
    }

    // Muda o estado de um sensor (ex: desativado ao receber BYE) e notifica o servidor.
    void AtualizarEstadoSensor(string id, string estado)
    {
        lock (fileLock)
        {
            if (_sensoresCache.TryGetValue(id, out var s))
                _sensoresCache[id] = (estado, s.Zona, s.Tipos, s.VideoStream, s.LastSync);
            PersistirCacheParaJson();
            RegistarLogEsquerda($"Sensor {id} -> {estado}.");
        }
        _ = NotificarServidorStatus(id, estado);
    }

    string ObterZonaDoSensor(string id)
    {
        lock (fileLock)
        {
            return _sensoresCache.TryGetValue(id, out var s) ? s.Zona : "ZONA_DESCONHECIDA";
        }
    }

    // Corre a cada 30s: marca como "manutencao" os sensores ativos sem heartbeat ha mais
    // de 30 segundos e avisa o servidor.
    void VerificarSensoresPerdidos()
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

    // Ciclo que verifica o buffer a cada 2 segundos.
    async Task LoopFlush(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await VerificarFlushTodos();
        }
        catch (OperationCanceledException) { }
    }

    // Coloca uma leitura no buffer da sua chave. Se o lote ficar cheio, faz flush imediato.
    void AdicionarAoBuffer(string sensorId, string zona, string tipoDado, double valor, DateTime timestamp, string? unidadeOverride = null)
    {
        string key   = $"{sensorId}.{tipoDado}";
        var    entry = _buffer.GetOrAdd(key, _ => new BufferEntry(new ConcurrentQueue<LeituraBuffer>(), DateTime.Now));

        string unidade = unidadeOverride ?? (_unidades.TryGetValue(tipoDado, out string? u) ? u : "");
        entry.Queue.Enqueue(new LeituraBuffer(sensorId, zona, tipoDado, unidade, valor, timestamp));

        if (_tipoConfigs.TryGetValue(tipoDado, out var cfg) && entry.Queue.Count >= cfg.ChunkSize)
            _ = Task.Run(() => FlushBuffer(key));
    }

    // Primeiro tenta reenviar lotes pendentes; depois faz flush por tempo dos buffers
    // que ja passaram do intervalo maximo; por fim drena o buffer de formatos brutos.
    async Task VerificarFlushTodos()
    {
        // Drena a fila de reenvio. Para no primeiro falhanco (servidor ainda inacessivel).
        int maxRetry = _pendentesJson.Count;
        for (int i = 0; i < maxRetry; i++)
        {
            if (!_pendentesJson.TryDequeue(out string? json)) break;
            var resp = await EnviarMensagemAoServidor(json);
            if (resp == null) { _pendentesJson.Enqueue(json); break; }
            RegistarLogDireita("[RETRY] Batch reenviado", $"ACK: {resp.Trim()}");
        }

        // Flush por tempo dos sensores lentos.
        foreach (string key in _buffer.Keys.ToList())
        {
            int dot = key.IndexOf('.');
            if (dot < 0) continue;
            string tipo = key[(dot + 1)..];

            if (!_tipoConfigs.TryGetValue(tipo, out var cfg)) continue;
            if (!_buffer.TryGetValue(key, out var bufEntry)) continue;
            if ((DateTime.Now - bufEntry.Inicio).TotalMilliseconds >= cfg.IntervaloMaxMs)
                await FlushBuffer(key);
        }

        await FlushRawBuffer();
    }

    // Esvazia o buffer de uma chave, normaliza o lote no PreProcessamento e encaminha
    // para o scoring e envio. TryRemove garante que so um flush processa o lote.
    async Task FlushBuffer(string key)
    {
        if (!_buffer.TryRemove(key, out var entry)) return;

        LeituraBuffer[] amostras = entry.Queue.ToArray();
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
                // Se a normalizacao falhar, segue com os valores brutos e qualidade reduzida.
                RegistarLogEsquerda($"[gRPC] Falha: {ex.Message}. A usar valores brutos.");
                validas.AddRange(amostras.Select(a => (a, a.Valor, 0.5f)));
            }
        }
        else
        {
            validas.AddRange(amostras.Select(a => (a, a.Valor, 1.0f)));
        }

        var finais = validas.Select(v => new LeituraFinal(
            v.Original.SensorId, v.Original.Zona, v.Original.TipoDado, v.Original.Unidade,
            v.ValorFinal, v.Original.Timestamp, v.Qualidade,
            AnomalyScore: null, IsAlarm: false
        )).ToList();

        if (finais.Count > 0)
            await ScoreEEnviar(finais);
    }

    // Ponto unico por onde passam todos os caminhos de ingestao (pipe e formatos brutos).
    // Avalia alarmes sobre valores ja normalizados, pede o scoring de anomalias ao
    // ServicoAnalise e envia o lote ao servidor.
    async Task ScoreEEnviar(List<LeituraFinal> finais)
    {
        for (int i = 0; i < finais.Count; i++)
            if (VerificarAlarme(finais[i]))
                finais[i] = finais[i] with { IsAlarm = true };

        if (_analiseClient != null)
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
                        Timestamp = f.Timestamp.ToString("yyyy-MM-ddTHH:mm:ssZ")
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
                                $"[ML] Anomalia: {finais[i].SensorId} {finais[i].TipoDado} score={a.Score:F2} - {a.Motivo}", true);
                    }
                }
            }
            catch (Exception ex) { RegistarLogEsquerda($"[ML] ScoreBatch falhou: {ex.Message}"); }
        }

        await EnviarBatchParaServidor(finais);
    }

    // Alarme por limiar: o valor ja vem em unidade padrao. O cooldown por sensor.tipo
    // evita que a mesma condicao dispare alarmes repetidos entre lotes.
    bool VerificarAlarme(LeituraFinal f)
    {
        bool acima;
        lock (_alarmesLock)
        {
            acima = _limitesAlarme.TryGetValue(f.Zona, out var z) &&
                    z.TryGetValue(f.TipoDado, out double limite) &&
                    limite != -1.0 && f.Valor > limite;
        }
        if (!acima) return false;

        string chave = $"{f.SensorId}.{f.TipoDado}";
        lock (_alarmeCooldownLock)
        {
            if (_ultimoAlarme.TryGetValue(chave, out DateTime ultimo) &&
                (DateTime.Now - ultimo).TotalSeconds < 30)
                return false;
            _ultimoAlarme[chave] = DateTime.Now;
        }

        RegistarLogEsquerda(
            $"EDGE ANALYTICS: Anomalia em {f.SensorId}! ({f.TipoDado} = {f.Valor:F1}{f.Unidade} @ {f.Zona})", true);
        return true;
    }

    // Drena o buffer de formatos brutos, envia-os ao PreProcessamento para detecao de
    // formato e encaminha as leituras validas (e de sensores registados) pelo pipeline.
    async Task FlushRawBuffer()
    {
        if (_rawBuffer.IsEmpty || _grpcClient == null) return;

        var bloco = new BlocoLeiturasBrutas();
        var zonas = new List<string>();
        while (_rawBuffer.TryDequeue(out var item))
        {
            bloco.Dados.Add(new LeituraBruta { GatewayId = _gatewayId, Zona = item.Zona, PayloadRede = item.RawPayload });
            zonas.Add(item.Zona);
        }
        if (bloco.Dados.Count == 0) return;

        try
        {
            var resultado = await _grpcClient.NormalizarBlocoAsync(bloco);

            var finais = new List<LeituraFinal>();
            for (int i = 0; i < Math.Min(resultado.Dados.Count, zonas.Count); i++)
            {
                var r = resultado.Dados[i];
                if (!r.Valido) { RegistarLogEsquerda($"[PreProc/Raw] Rejeitado: {r.Observacao}"); continue; }

                if (!ValidarSensor(r.SensorId, r.Tipo))
                {
                    RegistarLogEsquerda($"[RAW] {r.SensorId}: dados rejeitados - sensor nao registado ou tipo '{r.Tipo}' invalido.");
                    continue;
                }

                DateTime ts = DateTime.TryParse(r.Timestamp, null,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTime dt) ? dt : DateTime.UtcNow;

                finais.Add(new LeituraFinal(r.SensorId, zonas[i], r.Tipo, r.UnidadePadrao,
                    r.ValorNormalizado, ts, r.Qualidade, AnomalyScore: null, IsAlarm: false));
            }

            if (finais.Count > 0)
                await ScoreEEnviar(finais);
        }
        catch (Exception ex) { RegistarLogEsquerda($"[PreProc/Raw] gRPC falhou: {ex.Message}"); }
    }

    // Tenta interpretar uma mensagem de controlo (HELLO/HEARTBEAT/BYE) em JSON, XML ou
    // QueryString. Devolve true se reconheceu e tratou um comando de controlo.
    bool TryParsarControlo(string raw, string zonaRoteamento)
    {
        try
        {
            string t = raw.TrimStart();
            string cmd = "", sensorId = "", tipos = "";
            bool   videoStream = false;

            if (t.StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(t);
                var root = doc.RootElement;
                cmd        = root.TryGetProperty("comando",      out var c)  ? c.GetString()  ?? "" : "";
                sensorId   = root.TryGetProperty("sensor_id",   out var s)  ? s.GetString()  ?? "" : "";
                tipos      = root.TryGetProperty("tipos",       out var tp) ? tp.GetString() ?? "" : "";
                videoStream = root.TryGetProperty("videoStream", out var vs) && vs.GetBoolean();
            }
            else if (t.StartsWith('<'))
            {
                var xml = XDocument.Parse(t);
                if (xml.Root == null) return false;
                cmd        = xml.Root.Attribute("tipo")?.Value             ?? "";
                sensorId   = xml.Root.Element("SensorId")?.Value           ?? "";
                tipos      = xml.Root.Element("Tipos")?.Value              ?? "";
                videoStream = bool.TryParse(xml.Root.Element("VideoStream")?.Value, out bool vs) && vs;
            }
            else if (t.Contains('=') && !t.Contains('|'))
            {
                var vars = t.Split('&')
                            .Select(p => p.Split('='))
                            .Where(p => p.Length == 2)
                            .ToDictionary(p => p[0].Trim(),
                                          p => Uri.UnescapeDataString(p[1].Trim()),
                                          StringComparer.OrdinalIgnoreCase);
                cmd        = vars.GetValueOrDefault("cmd",   "");
                sensorId   = vars.GetValueOrDefault("id",    "");
                tipos      = vars.GetValueOrDefault("tipos", "");
                videoStream = bool.TryParse(vars.GetValueOrDefault("video", "false"), out bool vs) && vs;
            }
            else return false;

            switch (cmd.ToUpper())
            {
                case "HELLO" when sensorId.Length > 0:
                    RegistarOuAtualizarSensor(sensorId, zonaRoteamento, tipos, videoStream);
                    AutoPopularAlarmes(zonaRoteamento, tipos);
                    _ = EnviarRegistoSensorParaServidor(sensorId, zonaRoteamento, tipos, videoStream);
                    return true;
                case "HEARTBEAT" when sensorId.Length > 0:
                    AtualizarLastSync(sensorId);
                    return true;
                case "BYE" when sensorId.Length > 0:
                    AtualizarEstadoSensor(sensorId, "desativado");
                    return true;
                default:
                    return false;
            }
        }
        catch { return false; }
    }

    #endregion

    #region HANDLER DE SENSORES

    // Trata cada mensagem recebida do broker. As mensagens pipe (HELLO/DATA_SEND/etc) sao
    // tratadas aqui; os outros formatos vao para o buffer bruto (PreProcessamento).
    void ProcessarMensagemSensor(string rawData, string zonaRoteamento)
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

                        // So aceita dados de sensores registados e com o tipo declarado.
                        if (!ValidarSensor(sensorId, tipoDado))
                        {
                            RegistarLogEsquerda($"[WARN] {sensorId}: dados rejeitados. sensor nao registado ou tipo '{tipoDado}' invalido.");
                            break;
                        }

                        DateTime timestamp = DateTime.TryParse(parts[4], null,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out DateTime ts) ? ts : DateTime.UtcNow;
                        string zona         = ObterZonaDoSensor(sensorId).ToUpper();
                        string unidadePadrao = _unidades.TryGetValue(tipoDado, out string? uPad) ? uPad : "";
                        // O sexto campo (opcional) traz a unidade quando nao e a padrao.
                        string? unidadePipe  = parts.Length >= 6 && !string.IsNullOrEmpty(parts[5]) ? parts[5] : null;
                        string un            = unidadePipe ?? unidadePadrao;

                        AdicionarAoBuffer(sensorId, zona, tipoDado, valor, timestamp, unidadePipe);
                        RegistarLogEsquerda($"{sensorId}: {tipoDado} = {valor}{un}");
                    }
                    break;

                case "HEARTBEAT":
                    if (parts.Length >= 2) AtualizarLastSync(parts[1]);
                    break;

                case "BYE":
                    if (parts.Length >= 2) AtualizarEstadoSensor(parts[1], "desativado");
                    break;

                default:
                    // Token desconhecido: tenta como controlo noutro formato; se nao for,
                    // trata como dados e coloca no buffer bruto para o PreProcessamento.
                    if (!TryParsarControlo(rawData, zonaRoteamento))
                    {
                        if (_rawBuffer.Count >= MaxRawBuffer)
                        {
                            _rawBuffer.TryDequeue(out _);
                            RegistarLogEsquerda("[RAW] Fila cheia - payload mais antigo descartado.", true);
                        }
                        _rawBuffer.Enqueue((zonaRoteamento, rawData));
                    }
                    break;
            }
        }
        catch (Exception ex) { RegistarLogEsquerda($"Erro ao processar mensagem: {ex.Message}"); }
    }

    #endregion

    #region COMUNICACAO COM SERVIDOR

    // Envia uma mensagem ao servidor pela ligacao TCP persistente, com uma tentativa de
    // reconexao. Devolve a resposta, ou null se nao conseguir entregar.
    async Task<string?> EnviarMensagemAoServidor(string json)
    {
        await _serverConnLock.WaitAsync();
        try
        {
            for (int tentativa = 0; tentativa < 2; tentativa++)
            {
                try
                {
                    if (_serverTcp == null || !_serverTcp.Connected)
                    {
                        await ReconectarAoServidor();
                        await ReenviarRegistosSensoresInline();
                    }

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

    async Task ReconectarAoServidor()
    {
        _serverTcp = new TcpClient();
        await _serverTcp.ConnectAsync(_serverIp, 14000);
        var stream = _serverTcp.GetStream();
        _serverWriter = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        _serverReader = new StreamReader(stream, Encoding.UTF8);
        RegistarLogEsquerda($"[TCP] Ligado ao servidor {_serverIp}:14000.");
    }

    // Apos reconectar, reenvia o registo de todos os sensores conhecidos, para o servidor
    // recuperar o estado mesmo que tenha reiniciado durante a falha.
    async Task ReenviarRegistosSensoresInline()
    {
        List<(string Id, string Zona, string Tipos, bool VideoStream)> sensores;
        lock (fileLock)
            sensores = _sensoresCache
                .Select(kv => (kv.Key, kv.Value.Zona, kv.Value.Tipos, kv.Value.VideoStream))
                .ToList();

        foreach (var (id, zona, tipos, video) in sensores)
        {
            string regJson = JsonSerializer.Serialize(new
            {
                tipo = "SENSOR_REG", gatewayId = _gatewayId,
                sensorId = id, zona, tipos, videoStream = video
            }, _jsonWrite);
            try
            {
                await _serverWriter!.WriteLineAsync(regJson);
                await _serverReader!.ReadLineAsync();
            }
            catch { return; }
        }
        if (sensores.Count > 0)
            RegistarLogEsquerda($"[TCP] {sensores.Count} sensor(es) re-registados apos reconexao.");
    }

    // Serializa o lote como DATA_BATCH e envia-o. Se falhar, guarda na fila de reenvio
    // (descartando o mais antigo se a fila estiver cheia).
    async Task EnviarBatchParaServidor(List<LeituraFinal> leituras)
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
                RegistarLogEsquerda("[TCP] Fila cheia - batch mais antigo descartado.", true);
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

    // Envia o registo de um sensor ao servidor (inclui a porta de comando deste gateway).
    async Task EnviarRegistoSensorParaServidor(string sensorId, string zona, string tipos, bool videoCapable)
    {
        string json = JsonSerializer.Serialize(new
        {
            tipo        = "SENSOR_REG",
            gatewayId   = _gatewayId,
            sensorId, zona, tipos,
            videoStream = videoCapable,
            comandoPort = _comandoPort
        }, _jsonWrite);

        await EnviarMensagemAoServidor(json);
    }

    // Notifica o servidor de uma mudanca de estado de um sensor.
    async Task NotificarServidorStatus(string sensorId, string estado)
    {
        string json = JsonSerializer.Serialize(new
        {
            tipo      = "SENSOR_STATUS",
            gatewayId = _gatewayId,
            sensorId, estado
        }, _jsonWrite);

        await EnviarMensagemAoServidor(json);
    }

    // Recarrega os lotes pendentes do ficheiro no arranque.
    void CarregarPendentes()
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

    // Guarda os lotes pendentes em ficheiro (ou apaga-o se a fila estiver vazia).
    void SalvarPendentes()
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

    // Trata o Ctrl+C: para o ciclo de flush, guarda alarmes e pendentes, e termina.
    void TratarEncerramento(object? sender, ConsoleCancelEventArgs args)
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
