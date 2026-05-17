using System;
using System.IO;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using System.Globalization;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Serialization;
using Timer = System.Timers.Timer;
using Amqp;
using Amqp.Framing;

// ==========================================
// DTOs — JSON configs
// ==========================================
class AgregacaoConfig
{
    [JsonPropertyName("tipo")] public string Tipo { get; set; }
    [JsonPropertyName("unidade")] public string Unidade { get; set; }
    [JsonPropertyName("intervaloMs")] public int IntervaloMs { get; set; }
}

class ConfigGateway
{
    [JsonPropertyName("gatewayId")] public string GatewayId { get; set; } = "Gateway_001";
    [JsonPropertyName("serverIp")] public string ServerIp { get; set; } = "127.0.0.1";
    [JsonPropertyName("zona")] public string Zona { get; set; } = "CHAVES_NORTE";
    [JsonPropertyName("agregacoes")] public List<AgregacaoConfig> Agregacoes { get; set; } = new();
}

partial class MyTcpListener
{
    #region CAMPOS

    static string _gatewayId = "Gateway_001";
    static string _serverIp = "127.0.0.1";
    static string _zonaGateway = "CHAVES_NORTE";
    static readonly string pastaProjeto = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\"));
    static readonly string caminhoAlarmes = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\config_alarmes.json"));
    static readonly object _bufferFileLock = new object();
    static readonly object _alarmesLock = new object();

    static readonly JsonSerializerOptions _jsonRead = new() { PropertyNameCaseInsensitive = true };
    static readonly JsonSerializerOptions _jsonWrite = new() { WriteIndented = true };
    static readonly List<Timer> _timersAgregacao = new();

    // Campos Publisher-Subscriber AMQP Connection
    private static Connection _connection;  //Conexão com o brooker
    private static Session _session;    //Sessão de comunicação
    private static SenderLink _sender;  //Interface do publicador
    private static ReceiverLink _receiverTelemetria;
    private static ReceiverLink _receiverComandos;
    private static readonly Dictionary<string, SenderLink> _sendersParaSensores = new();    // Interface para enviar comandos a sensores, em caso de request de video
    static readonly Dictionary<string, string> _unidadesMedida = new();
    static Dictionary<string, Dictionary<string, double>> _limitesAlarme = new();
    static readonly Dictionary<string, long> _janelasTemporais = new();

    private static bool _isOnline = false;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _sensorZonaCache = new();

    #endregion

    public static void Main()
    {
        Console.CancelKeyPress += TratarEncerramento;

        InicializarTimersGateway();
        InicializarFicheiroAlarmesJson();

        try
        {
            RegistarLogEsquerda("A iniciar ligação ao Broker AMQP (127.0.0.1:5672)...");

            Address address = new Address("amqp://guest:guest@127.0.0.1:5672");

            _connection = new Connection(address);

            _session = new Session(_connection);

            // Subscritor para telemetria de sensores da zona CHAVES_NORTE
            _receiverTelemetria = new ReceiverLink(_session, "gateway-telemetry-receiver", $"/queues/sensor.telemetry.{_zonaGateway}");

            _receiverTelemetria.Start(100, OnMensagemRecebidaAmqp);

            // Subscritor de Comandos
            _receiverComandos = new ReceiverLink(_session, "gateway-commands-receiver", $"/queues/gateway.commands.{_gatewayId}");

            _receiverComandos.Start(10, (receiver, message) =>
            {
                receiver.Accept(message);
                string cmd = message.Body.ToString();
                ProcessarComandoDoServidor(cmd);
            });
            RegistarLogEsquerda("[DEBUG] ReceiverLink comandos iniciado!");

            _isOnline = true;
            RegistarLogEsquerda($"Gateway ligado ao Broker. Filtrando zona: {_zonaGateway}");

            while (true)
            {
                Thread.Sleep(1000);
            }
        }
        catch (Exception ex)
        {
            RegistarLogEsquerda($"Erro fatal no Broker: {ex.Message}");
        }
        finally
        {
            _connection?.Close();
        }
    }

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

    #endregion

    #region CONFIG DO GATEWAY

    static void InicializarTimersGateway()
    {
        string caminho = Path.Combine(pastaProjeto, "config_gateway.json");

        if (!File.Exists(caminho))
        {
            var def = new ConfigGateway
            {
                GatewayId = _gatewayId,
                Zona = _zonaGateway,
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
        _serverIp = cfg.ServerIp;
        _zonaGateway = cfg.Zona;

        foreach (var ag in cfg.Agregacoes)
        {
            string tipo = ag.Tipo.ToUpper();
            _unidadesMedida[tipo] = ag.Unidade;
            _janelasTemporais[tipo] = DateTime.Now.Ticks;

            Timer t = new Timer(ag.IntervaloMs);
            t.Elapsed += (_, _) => ProcessarAgregadosFiltrados(tipo);
            t.AutoReset = true;
            t.Start();
            _timersAgregacao.Add(t);
        }
    }

    #endregion

    #region REGISTO DE SENSORES

    static void OnMensagemRecebidaAmqp(IReceiverLink receiver, Message message)
    {
        try
        {
            receiver.Accept(message); // ACK ao broker

            string rawData = message.Body.ToString() ?? "";
            RegistarLogEsquerda($"[DEBUG BROKER] Chegou pacote cru: {rawData}");
            string[] parts = rawData.Split('|');
            string command = parts[0].ToUpper();

            switch (command)
            {
                case "HELLO":
                    // HELLO|sensorId|zona|[tipos]|videoStream
                    if (parts.Length >= 4)
                    {
                        string sensorId = parts[1];
                        string zona = parts[2].ToUpper();
                        string tipos = parts[3];
                        bool videoCapable = parts.Length >= 5 && bool.TryParse(parts[4], out bool vc) && vc;

                        // Guardar zona do sensor em cache (para usar no DATA_SEND)
                        _sensorZonaCache[sensorId] = zona;

                        RegistarLogEsquerda($"HELLO: {sensorId} (zona: {zona})");
                        AutoPopularAlarmes(zona, tipos);

                        // Enviar registo ao servidor via TCP
                        EnviarRegistoSensorParaServidor(sensorId, zona, tipos, videoCapable);
                    }
                    break;
                case "DATA_SEND":
                    // DATA_SEND|sensorId|tipoDado|valor|timestamp
                    if (parts.Length >= 5 &&
                        double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out double valor))
                    {
                        string sensorId = parts[1];
                        string tipoDado = parts[2].ToUpper();
                        string valorStr = parts[3];
                        string timestamp = parts[4];

                        // Verificar se conhecemos este sensor (se passou pelo HELLO)
                        if (!_sensorZonaCache.TryGetValue(sensorId, out string zona))
                        {
                            RegistarLogEsquerda($"[AVISO] Sensor {sensorId} desconhecido. A pedir reidentificação...");
                            EnviarComandoParaSensor(sensorId, "REQUEST_HELLO"); // <-- ADICIONAR ESTA LINHA
                            return;
                        }

                        // SEMPRE armazenar em buffer para agregação (mesmo anomalias)
                        long tickAtivo;
                        lock (_bufferFileLock)
                        {
                            tickAtivo = _janelasTemporais.TryGetValue(tipoDado, out long tk) ? tk : DateTime.Now.Ticks;
                        }
                        File.AppendAllText(
                            Path.Combine(pastaProjeto, $"pendente_{tickAtivo}_{sensorId}_{tipoDado}.csv"),
                            valor.ToString(CultureInfo.InvariantCulture) + Environment.NewLine);

                        string un = _unidadesMedida.TryGetValue(tipoDado, out string u) ? u : "";

                        // Edge Analytics: Verificar anomalias
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
                                $"⚠ ANOMALIA: {sensorId} | {tipoDado} = {valor}{un} @ {zona}", true);

                            // Enviar alarme IMEDIATO ao servidor via TCP
                            EnviarAlarmeParaServidor(sensorId, zona, tipoDado, valorStr, timestamp);
                        }
                        else
                        {
                            RegistarLogEsquerda($"{sensorId}: {tipoDado} = {valor}{un}");
                        }
                    }
                    break;

                case "HEARTBEAT":
                    // Sensor ainda está vivo
                    if (parts.Length >= 2)
                        RegistarLogEsquerda($"{parts[1]} heartbeat", false);
                    break;

                case "BYE":
                    // Sensor a desligar - remover do cache
                    if (parts.Length >= 2)
                    {
                        string sensorId = parts[1];
                        _sensorZonaCache.TryRemove(sensorId, out _);
                        RegistarLogEsquerda($"Sensor {sensorId} desconectado.");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            RegistarLogEsquerda($"Erro ao processar telemetria: {ex.Message}");
        }
    }

    #endregion

    #region AGREGAÇÃO

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

                string ticksStr = partes[1];
                string sensorId = partes[2];
                string tipoDado = partes[3];

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

                    // Colocar aqui comunicação com servidor externo, via gRPC

                    RegistarLogEsquerda($"Falha Servidor: Ficheiro de {sensorId} ({tipoDado}) retido.", true);

                    // Apagar ficheiro após envio bem-sucedido
                    File.Delete(ficheiro);
                }
            }
            catch (Exception ex) { RegistarLogEsquerda($"Erro pendente: {ex.Message}"); }
        }
    }

    #endregion

    #region COMANDOS PARA SENSORES

    static void ProcessarComandoDoServidor(string comando)
    {
        string[] partes = comando.Split('|');

        if (partes[0] == "REQUEST_STREAM" && partes.Length >= 4)
        {
            string sensorId = partes[1];
            string serverIp = partes[2];
            string udpPort = partes[3];

            RegistarLogEsquerda($"Servidor solicita stream de {sensorId} para {serverIp}:{udpPort}");
            EnviarComandoParaSensor(sensorId, $"STREAM_TO|{serverIp}:{udpPort}");
        }
        else if (partes[0] == "STOP_STREAM" && partes.Length >= 2)
        {
            RegistarLogEsquerda($"Servidor solicita parar stream de {partes[1]}");
            EnviarComandoParaSensor(partes[1], "STOP_STREAM");
        }
    }

    static void EnviarComandoParaSensor(string sensorId, string comando)
    {
        try
        {
            if (!_sendersParaSensores.TryGetValue(sensorId, out var sender))
            {
                // Criar sender específico para este sensor (cache)
                sender = new SenderLink(_session, $"cmd-to-{sensorId}", $"/queues/sensor.commands.{sensorId}");
                _sendersParaSensores[sensorId] = sender;
            }

            Message msg = new Message(comando);
            msg.Properties = new Properties() { Subject = "command", MessageId = Guid.NewGuid().ToString() };
            sender.Send(msg);

            RegistarLogEsquerda($"→ Comando enviado para {sensorId}: {comando}");
        }
        catch (Exception ex)
        {
            RegistarLogEsquerda($"Erro ao enviar comando para {sensorId}: {ex.Message}");
        }
    }

    #endregion

    #region COMUNICAÇÃO COM SERVIDOR
    static void EnviarAlarmeParaServidor(string sensorId, string zona, string tipoDado, string valor, string timestamp)
    {
        try
        {
            using TcpClient sc = new TcpClient(_serverIp, 14000);
            using var s = sc.GetStream();
            using StreamReader r = new StreamReader(s);
            using StreamWriter w = new StreamWriter(s) { AutoFlush = true };

            w.WriteLine($"ALARM_FORWARD|{_gatewayId}|{sensorId}|{zona}|{tipoDado}|{valor}|{timestamp}");

            string resposta = r.ReadLine();
            string un = _unidadesMedida.TryGetValue(tipoDado, out string u) ? u : "";
            RegistarLogDireita($"ENVIADO: [ALARM] {sensorId} ({tipoDado}={valor}{un})", $"RESPOSTA: {resposta}");
        }
        catch (Exception ex)
        {
            RegistarLogDireita("ENVIADO: [Tentativa Falhada]", $"ERRO: {ex.Message}");
        }
    }


    static void EnviarRegistoSensorParaServidor(string sensorId, string zona, string tipos, bool videoCapable) // Envia para o servidor os dados dos sensores, que receberá do serviço externo
    {
        try
        {
            using TcpClient sc = new TcpClient(_serverIp, 14000);
            using var s = sc.GetStream();
            using StreamReader r = new StreamReader(s);
            using StreamWriter w = new StreamWriter(s) { AutoFlush = true };

            w.WriteLine($"SENSOR_REG|{_gatewayId}|{sensorId}|{zona}|{tipos}|{(videoCapable ? "true" : "false")}");
            r.ReadLine(); // consume ACK
        }
        catch { /* Server may not be running yet; sensor will re-HELLO on reconnect */ }
    }

    // TODO: SEU COLEGA - Método para receber dados tratados do serviço externo
    // e encaminhar ao servidor
    // static void EnviarDadosTratadosParaServidor(string sensorId, string zona, string tipoDado, string valor, string timestamp)
    // {
    //     ... (igual a EnviarAlarmeParaServidor mas com "DATA_FORWARD")
    // }

    #endregion

    #region ENCERRAMENTO

    static void TratarEncerramento(object sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        _isOnline = false;
        RegistarLogEsquerda("A encerrar o Gateway. Dados em buffer salvaguardados.");

        foreach (var t in _timersAgregacao) t.Stop();
        GuardarAlarmesJson();

        // Fechar conexões AMQP
        foreach (var senderLink in _sendersParaSensores.Values)
            senderLink.Close();

        _receiverTelemetria?.Close();
        _receiverComandos?.Close();
        _session?.Close();
        _connection?.Close();

        Thread.Sleep(500);
        Environment.Exit(0);
    }

    #endregion
}
