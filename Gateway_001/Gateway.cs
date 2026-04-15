using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using System.Globalization;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Serialization;
using Timer = System.Timers.Timer;

// ==========================================
// DTOs for JSON configs
// ==========================================
class AgregacaoConfig
{
    [JsonPropertyName("tipo")]        public string Tipo        { get; set; }
    [JsonPropertyName("unidade")]     public string Unidade     { get; set; }
    [JsonPropertyName("intervaloMs")] public int    IntervaloMs { get; set; }
}

class ConfigGateway
{
    [JsonPropertyName("gatewayId")]   public string              GatewayId  { get; set; } = "Gateway_001";
    [JsonPropertyName("agregacoes")]  public List<AgregacaoConfig> Agregacoes { get; set; } = new();
}

class SensorEntry
{
    [JsonPropertyName("id")]          public string Id          { get; set; }
    [JsonPropertyName("status")]      public string Status      { get; set; }
    [JsonPropertyName("zona")]        public string Zona        { get; set; }
    [JsonPropertyName("tipos")]       public string Tipos       { get; set; }
    [JsonPropertyName("videoStream")] public bool   VideoStream { get; set; }
    [JsonPropertyName("lastSync")]    public string LastSync    { get; set; }
}

class MyTcpListener
{
    static string _gatewayId = "Gateway_001";

    static readonly string pastaProjeto  = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\"));
    static readonly string caminhoSensores = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\sensores.json"));
    static readonly string caminhoAlarmes  = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\config_alarmes.json"));

    static readonly object fileLock       = new object();
    static readonly object _bufferFileLock = new object();
    static readonly object _alarmesLock   = new object();

    // Cached JSON options
    static readonly JsonSerializerOptions _jsonRead  = new() { PropertyNameCaseInsensitive = true };
    static readonly JsonSerializerOptions _jsonWrite = new() { WriteIndented = true };

    // In-memory sensor registry: (Status, Zona, Tipos, VideoStream, LastSync)
    // Eliminates file reads on every DATA_SEND — validated under fileLock.
    static readonly Dictionary<string, (string Status, string Zona, string Tipos, bool VideoStream, DateTime LastSync)>
        _sensoresCache = new();

    static readonly List<Timer> _timersAgregacao = new();
    static Timer _timerWatchdog;
    static TcpListener server = null;

    static readonly Dictionary<string, string> _unidadesMedida  = new();
    static          Dictionary<string, Dictionary<string, double>> _limitesAlarme = new();
    static readonly Dictionary<string, long>   _janelasTemporais = new();

    private static readonly object       _consoleLock   = new object();
    private static readonly List<string> _alarmesEsquerda = new();
    private static readonly List<string> _logsEsquerda    = new();
    private static readonly List<string> _logsDireita      = new();
    private static bool _isOnline = true;

    public static void Main()
    {
        Console.CancelKeyPress += TratarEncerramento;
        InicializarSensoresJson();
        InicializarTimersGateway();
        InicializarFicheiroAlarmesJson();

        _timerWatchdog = new Timer(10000);
        _timerWatchdog.Elapsed += VerificarSensoresPerdidos;
        _timerWatchdog.AutoReset = true;
        _timerWatchdog.Start();

        try
        {
            server = new TcpListener(IPAddress.Any, 5000);
            server.Start();
            RegistarLogEsquerda("A escuta de sensores na porta 5000...");

            while (true)
            {
                TcpClient client = server.AcceptTcpClient();
                new Thread(() => HandleSensor(client)).Start();
            }
        }
        catch (SocketException) { RegistarLogEsquerda("Sistema de escuta interrompido."); }
        finally { server?.Stop(); }
    }

    // ==========================================
    // ALARMES JSON
    // ==========================================
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
        File.WriteAllText(caminhoAlarmes, JsonSerializer.Serialize(_limitesAlarme, _jsonWrite));
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

    // ==========================================
    // GATEWAY CONFIG (config_gateway.json)
    // ==========================================
    static void InicializarTimersGateway()
    {
        string caminho = Path.Combine(pastaProjeto, "config_gateway.json");

        if (!File.Exists(caminho))
        {
            var def = new ConfigGateway
            {
                GatewayId = _gatewayId,
                Agregacoes = new()
                {
                    new AgregacaoConfig { Tipo = "TEMP",  Unidade = "ºC",  IntervaloMs = 30000 },
                    new AgregacaoConfig { Tipo = "HUM",   Unidade = "%",   IntervaloMs = 60000 },
                    new AgregacaoConfig { Tipo = "CO2",   Unidade = "ppm", IntervaloMs = 90000 },
                    new AgregacaoConfig { Tipo = "RUIDO", Unidade = "dB",  IntervaloMs = 40000 }
                }
            };
            File.WriteAllText(caminho, JsonSerializer.Serialize(def, _jsonWrite));
        }

        var cfg = JsonSerializer.Deserialize<ConfigGateway>(File.ReadAllText(caminho), _jsonRead)!;
        _gatewayId = cfg.GatewayId;

        foreach (var ag in cfg.Agregacoes)
        {
            string tipo = ag.Tipo.ToUpper();
            _unidadesMedida[tipo]  = ag.Unidade;
            _janelasTemporais[tipo] = DateTime.Now.Ticks;

            Timer t = new Timer(ag.IntervaloMs);
            t.Elapsed += (_, _) => ProcessarAgregadosFiltrados(tipo);
            t.AutoReset = true;
            t.Start();
            _timersAgregacao.Add(t);
        }
    }

    // ==========================================
    // SENSOR REGISTRY (sensores.json)
    // ==========================================
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

    // Rebuilds sensores.json from the in-memory cache. Must be called inside fileLock.
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

        File.WriteAllText(caminhoSensores, JsonSerializer.Serialize(lista, _jsonWrite));
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
    }

    // O(1) cache lookup — no file I/O on the hot DATA_SEND path
    static bool ValidarSensor(string id, string tipoDados)
    {
        lock (fileLock)
        {
            return _sensoresCache.TryGetValue(id, out var s) && s.Status == "ativo" && s.Tipos.Contains(tipoDados);
        }
    }

    // Heartbeats update only the cache — no disk write every 5 seconds per sensor
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
            RegistarLogEsquerda($"Sensor {id} {estado}.");
        }
    }

    static string ObterZonaDoSensor(string id)
    {
        lock (fileLock)
        {
            return _sensoresCache.TryGetValue(id, out var s) ? s.Zona : "ZONA DESCONHECIDA";
        }
    }

    static void VerificarSensoresPerdidos(object sender, ElapsedEventArgs e)
    {
        lock (fileLock)
        {
            bool alterado = false;
            foreach (var id in _sensoresCache.Keys.ToList())
            {
                var s = _sensoresCache[id];
                if (s.Status == "ativo" && (DateTime.Now - s.LastSync).TotalSeconds > 30)
                {
                    _sensoresCache[id] = ("perdido", s.Zona, s.Tipos, s.VideoStream, s.LastSync);
                    alterado = true;
                    RegistarLogEsquerda($"Watchdog: Sensor {id} perdido (Timeout).", true);
                }
            }
            if (alterado) PersistirCacheParaJson();
        }
    }

    // ==========================================
    // AGREGAÇÃO
    // ==========================================
    static void ProcessarAgregadosFiltrados(string tipoDadoFiltro)
    {
        long tickNovo;
        lock (_bufferFileLock)
        {
            tickNovo = DateTime.Now.Ticks;
            _janelasTemporais[tipoDadoFiltro] = tickNovo;
        }

        foreach (string ficheiro in Directory.GetFiles(pastaProjeto, $"pendente_*_{tipoDadoFiltro}.csv"))
        {
            if (ficheiro.Contains($"pendente_{tickNovo}_")) continue;

            try
            {
                string[] partes = Path.GetFileNameWithoutExtension(ficheiro).Split('_');
                if (partes.Length != 4) continue;

                string ticksStr  = partes[1];
                string sensorId  = partes[2];
                string tipoDado  = partes[3];

                string[] linhas = File.ReadAllLines(ficheiro);
                if (linhas.Length == 0) { File.Delete(ficheiro); continue; }

                var valores = linhas
                    .Where(l => double.TryParse(l, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                    .Select(l => double.Parse(l, NumberStyles.Any, CultureInfo.InvariantCulture))
                    .ToList();

                if (valores.Count > 0)
                {
                    double media = valores.Average();
                    string ts = long.TryParse(ticksStr, out long ticks)
                        ? new DateTime(ticks).ToString("yyyy-MM-ddTHH:mm:ss")
                        : DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

                    if (EnviarParaServidor("DATA_FORWARD", sensorId, tipoDado,
                            media.ToString("F2", CultureInfo.InvariantCulture), ts))
                    {
                        File.Delete(ficheiro);
                        RegistarLogEsquerda($"Forward pendente de {sensorId} ({tipoDado}) OK.");
                    }
                    else RegistarLogEsquerda($"Falha Servidor: Ficheiro de {sensorId} ({tipoDado}) retido.", true);
                }
            }
            catch (Exception ex) { RegistarLogEsquerda($"Erro pendente: {ex.Message}"); }
        }
    }

    // ==========================================
    // HANDLER DE SENSORES
    // ==========================================
    static void HandleSensor(TcpClient client)
    {
        try
        {
            using NetworkStream stream = client.GetStream();
            using StreamReader  reader = new StreamReader(stream);
            using StreamWriter  writer = new StreamWriter(stream) { AutoFlush = true };

            string rawData;
            while ((rawData = reader.ReadLine()) != null)
            {
                string[] parts  = rawData.Split('|');
                string command  = parts[0].ToUpper();
                string resposta = "ACK_OK";

                switch (command)
                {
                    case "HELLO":
                        if (parts.Length >= 4)
                        {
                            // parts[4] = "true"/"false" — video streaming capability
                            bool videoCapable = parts.Length >= 5 &&
                                                bool.TryParse(parts[4], out bool vc) && vc;

                            RegistarOuAtualizarSensor(parts[1], parts[2], parts[3], videoCapable);
                            AutoPopularAlarmes(parts[2], parts[3]);

                            // Notify server about this sensor (no locks held here)
                            EnviarRegistoSensorParaServidor(parts[1], parts[2], parts[3], videoCapable);
                        }
                        resposta = "ACK_HELLO|OK";
                        break;

                    case "DATA_SEND":
                        if (parts.Length >= 5 &&
                            double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out double valor))
                        {
                            if (ValidarSensor(parts[1], parts[2]))
                            {
                                string sensorId  = parts[1];
                                string tipoDado  = parts[2].ToUpper();
                                string timestamp = parts[4];
                                string zona      = ObterZonaDoSensor(sensorId).ToUpper();

                                // Read the tick under lock; do file I/O outside to minimise lock hold time
                                long tickAtivo;
                                lock (_bufferFileLock)
                                {
                                    tickAtivo = _janelasTemporais.TryGetValue(tipoDado, out long tk)
                                        ? tk : DateTime.Now.Ticks;
                                }
                                File.AppendAllText(
                                    Path.Combine(pastaProjeto, $"pendente_{tickAtivo}_{sensorId}_{tipoDado}.csv"),
                                    valor.ToString(CultureInfo.InvariantCulture) + Environment.NewLine);

                                string un = _unidadesMedida.TryGetValue(tipoDado, out string u) ? u : "";

                                bool isAnomalia = false;
                                lock (_alarmesLock)
                                {
                                    if (_limitesAlarme.TryGetValue(zona, out var z) &&
                                        z.TryGetValue(tipoDado, out double limite) &&
                                        limite != -1.0 && valor > limite)
                                        isAnomalia = true;
                                }

                                if (isAnomalia)
                                {
                                    RegistarLogEsquerda(
                                        $"EDGE ANALYTICS: Anomalia em {sensorId}! ({tipoDado} = {valor}{un} @ {zona})", true);

                                    // Log whether this sensor can provide a video feed
                                    bool temVideo;
                                    lock (fileLock)
                                    {
                                        temVideo = _sensoresCache.TryGetValue(sensorId, out var sc) && sc.VideoStream;
                                    }
                                    if (temVideo)
                                        RegistarLogEsquerda($"[VIDEO] {sensorId} tem capacidade de streaming.", true);

                                    EnviarParaServidor("ALARM_FORWARD", sensorId, tipoDado, parts[3], timestamp);
                                    resposta = "ACK_DATA|ALARM_DETECTED";
                                }
                                else
                                {
                                    RegistarLogEsquerda($"{sensorId}: {tipoDado} = {valor}{un}");
                                    resposta = "ACK_DATA|OK";
                                }
                            }
                            else resposta = "ACK_DATA|ERRO_VALIDACAO";
                        }
                        break;

                    case "HEARTBEAT":
                        AtualizarLastSync(parts[1]);
                        resposta = "ACK_HEARTBEAT|OK";
                        break;

                    case "BYE":
                        if (parts.Length >= 2) AtualizarEstadoSensor(parts[1], "desativado");
                        resposta = "ACK_BYE|OK";
                        break;

                    default:
                        resposta = "ACK_ERR|Comando Desconhecido";
                        break;
                }

                writer.WriteLine(resposta);
                if (command == "BYE") break;
            }
        }
        catch (Exception ex) { RegistarLogEsquerda($"Erro no handler do sensor: {ex.Message}"); }
        finally { client.Close(); }
    }

    // ==========================================
    // COMUNICAÇÃO COM SERVIDOR
    // ==========================================
    static bool EnviarParaServidor(string tipo, string sensorId, string tipoDado, string valor, string timestamp)
    {
        try
        {
            using TcpClient  sc = new TcpClient("127.0.0.1", 14000);
            using var         s = sc.GetStream();
            using StreamReader r = new StreamReader(s);
            using StreamWriter w = new StreamWriter(s) { AutoFlush = true };

            string zona = ObterZonaDoSensor(sensorId);
            w.WriteLine($"{tipo}|{_gatewayId}|{sensorId}|{zona}|{tipoDado}|{valor}|{timestamp}");

            string resposta = r.ReadLine();
            string tag = tipo == "ALARM_FORWARD" ? "[ALARM]" : "[DATA]";
            string un  = _unidadesMedida.TryGetValue(tipoDado, out string u) ? u : "";
            RegistarLogDireita($"ENVIADO: {tag} {sensorId} ({tipoDado}={valor}{un})", $"RESPOSTA: {resposta}");

            return resposta != null && resposta.Contains("STATUS OK");
        }
        catch (Exception ex)
        {
            RegistarLogDireita("ENVIADO: [Tentativa Falhada]", $"ERRO: {ex.Message}");
            return false;
        }
    }

    // Informs the server that a sensor registered with the gateway (and whether it has video capability).
    // Called with no locks held — may fail silently if server is not yet running.
    static void EnviarRegistoSensorParaServidor(string sensorId, string zona, string tipos, bool videoCapable)
    {
        try
        {
            using TcpClient  sc = new TcpClient("127.0.0.1", 14000);
            using var         s = sc.GetStream();
            using StreamReader r = new StreamReader(s);
            using StreamWriter w = new StreamWriter(s) { AutoFlush = true };

            w.WriteLine($"SENSOR_REG|{_gatewayId}|{sensorId}|{zona}|{tipos}|{(videoCapable ? "true" : "false")}");
            r.ReadLine(); // consume ACK
        }
        catch { /* Server may not be running yet; no retry needed — sensor will re-HELLO on reconnect */ }
    }

    // ==========================================
    // TUI
    // ==========================================
    static void RegistarLogEsquerda(string mensagem, bool isAlarm = false)
    {
        lock (_consoleLock)
        {
            string linha = $"[{DateTime.Now:HH:mm:ss}] {mensagem}";
            if (isAlarm)
            {
                _alarmesEsquerda.Insert(0, linha);
                if (_alarmesEsquerda.Count > 10) _alarmesEsquerda.RemoveAt(10);
            }
            else
            {
                _logsEsquerda.Insert(0, linha);
                if (_logsEsquerda.Count > 10) _logsEsquerda.RemoveAt(10);
            }
            DesenharDashboard();
        }
    }

    static void RegistarLogDireita(string msgEnvio, string msgResposta)
    {
        lock (_consoleLock)
        {
            string t = DateTime.Now.ToString("HH:mm:ss");
            _logsDireita.Insert(0, $"   └─> {msgResposta}");
            _logsDireita.Insert(0, $"[{t}] {msgEnvio}");
            while (_logsDireita.Count > 20) _logsDireita.RemoveAt(_logsDireita.Count - 1);
            DesenharDashboard();
        }
    }

    static void DesenharDashboard()
    {
        try { Console.SetCursorPosition(0, 0); } catch { Console.Clear(); }
        Console.CursorVisible = false;

        string sep = new string('=', 118);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(sep);
        Console.WriteLine("                                            [ ONE HEALTH - GATEWAY EDGE ]                                           ");
        Console.WriteLine(sep);
        Console.ResetColor();

        Console.Write("  ESTADO: ");
        if (_isOnline) { Console.ForegroundColor = ConsoleColor.Green; Console.Write("ONLINE "); }
        else           { Console.ForegroundColor = ConsoleColor.Red;   Console.Write("OFFLINE"); }
        Console.ResetColor();
        Console.WriteLine($"   |   NODE ID: {_gatewayId}".PadRight(90));
        Console.WriteLine(sep);

        var leftCol  = new List<string>();
        var rightCol = new List<string>();

        leftCol.Add("[ ALARMES & EVENTOS CRITICOS ]");
        if (_alarmesEsquerda.Count == 0) leftCol.Add("   Sem ocorrencias.");
        else foreach (var a in _alarmesEsquerda) leftCol.Add("!!! " + a);
        while (leftCol.Count < 12) leftCol.Add("");

        leftCol.Add("[ TRAFEGO RECEBIDO (SENSORES) ]");
        foreach (var l in _logsEsquerda) leftCol.Add("> " + l);
        while (leftCol.Count < 24) leftCol.Add("");

        rightCol.Add("[ OUTPUT PARA O SERVIDOR CENTRAL ]");
        foreach (var r in _logsDireita) rightCol.Add(r);
        while (rightCol.Count < 24) rightCol.Add("");

        for (int i = 0; i < 24; i++)
        {
            string left  = leftCol[i].Length  > 56 ? leftCol[i].Substring(0, 53)  + "..." : leftCol[i];
            string right = rightCol[i].Length > 58 ? rightCol[i].Substring(0, 55) + "..." : rightCol[i];

            if      (left.Contains("!!!") || left.Contains("Falha") || left.Contains("Watchdog")) Console.ForegroundColor = ConsoleColor.Red;
            else if (left.Contains("[ ALARMES") || left.Contains("[ TRAFEGO"))                   Console.ForegroundColor = ConsoleColor.Cyan;
            else if (left.Contains("[VIDEO]"))                                                    Console.ForegroundColor = ConsoleColor.Magenta;
            else                                                                                  Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write(left.PadRight(58));

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(" | ");

            if      (right.Contains("[ALARM]"))                         Console.ForegroundColor = ConsoleColor.Yellow;
            else if (right.Contains("[DATA]"))                          Console.ForegroundColor = ConsoleColor.White;
            else if (right.Contains("STATUS OK"))                       Console.ForegroundColor = ConsoleColor.Green;
            else if (right.Contains("ERRO") || right.Contains("Falha")) Console.ForegroundColor = ConsoleColor.Red;
            else if (right.Contains("[ OUTPUT"))                        Console.ForegroundColor = ConsoleColor.Cyan;
            else                                                         Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(right.PadRight(57));

            Console.ResetColor();
        }

        Console.WriteLine(sep);
        Console.WriteLine(" Pressione Ctrl+C para desligar o Gateway de forma segura.".PadRight(118));
    }

    static void TratarEncerramento(object sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        _isOnline = false;
        RegistarLogEsquerda("A encerrar o Gateway. Dados em buffer salvaguardados.");

        foreach (var t in _timersAgregacao) t.Stop();
        _timerWatchdog?.Stop();
        GuardarAlarmesJson();

        server?.Stop();
        Thread.Sleep(500);
        Environment.Exit(0);
    }
}
