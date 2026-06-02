using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Timers;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Timer = System.Timers.Timer;

namespace sensor
{
    #region CONFIG (DTOs)

    class LeituraConfig
    {
        [JsonPropertyName("tipo")]        public string Tipo        { get; set; } = "";
        [JsonPropertyName("intervaloMs")] public int    IntervaloMs { get; set; }
    }

    class ConfigSensor
    {
        [JsonPropertyName("sensorId")]     public string              SensorId     { get; set; } = "S???";
        [JsonPropertyName("zona")]         public string              Zona         { get; set; } = "DESCONHECIDA";
        [JsonPropertyName("videoStream")]  public bool                VideoStream  { get; set; } = false;
        [JsonPropertyName("zonaType")]     public string              ZonaType     { get; set; } = "residencial";
        [JsonPropertyName("rabbitMqHost")] public string              RabbitMqHost { get; set; } = "localhost";
        [JsonPropertyName("leituras")]     public List<LeituraConfig> Leituras     { get; set; } = new();
    }

    class SensorConfig
    {
        public string TipoDado    { get; set; } = "";
        public int    IntervaloMs { get; set; }
    }

    #endregion

    partial class Program
    {
        #region CAMPOS

        static string _idSensor    = "S???";
        static string _zona        = "DESCONHECIDA";
        static bool   _videoStream = false;
        static string _dataTypes   = "";
        static string _brokerHost  = "localhost";

        static IConnection? _connection;
        static IChannel?    _channel;

        static Timer? _timerHeartbeat;
        static readonly List<Timer> _timersDados        = new();
        static readonly int         _intervaloHeartbeat = 5000;

        private static readonly object       _consoleLock = new object();
        private static readonly List<string> _ultimosLogs = new();

        private static readonly JsonSerializerOptions _jsonRead  = new() { PropertyNameCaseInsensitive = true };
        private static readonly JsonSerializerOptions _jsonWrite = new() { WriteIndented = true };

        private static volatile bool _isOnline   = false;
        private static volatile bool _encerrando = false;
        private static string _brokerLabel = "";
        private static string _zonaType    = "residencial";

        private static readonly System.Text.StringBuilder _debugInput = new();

        private static volatile bool _streamingAtivo = false;
        private static Thread?       _threadStream   = null;

        private const string EXCHANGE = "one_health";

        #endregion

        #region INICIALIZAÇÃO

        static async Task Main(string[] args)
        {
            Console.CancelKeyPress += TratarEncerramento;

            List<SensorConfig> configs = CarregarConfiguracoes();
            DesenharDashboard();
            ConfigurarTemporizadores(configs);
            IniciarMenuDebug();

            while (!_encerrando)
            {
                try
                {
                    AlterarEstado(false, "A LIGAR...");

                    var factory = new ConnectionFactory { HostName = _brokerHost };
                    _connection = await factory.CreateConnectionAsync();
                    _channel    = await _connection.CreateChannelAsync();

                    await _channel.ExchangeDeclareAsync(EXCHANGE, ExchangeType.Topic, durable: true, autoDelete: false);

                    await _channel.QueueDeclareAsync($"commands.{_idSensor}", durable: false, exclusive: true, autoDelete: true);
                    var consumer = new AsyncEventingBasicConsumer(_channel);
                    consumer.ReceivedAsync += async (_, ea) =>
                    {
                        string cmd = Encoding.UTF8.GetString(ea.Body.ToArray());
                        ProcessarComando(cmd);
                        await _channel.BasicAckAsync(ea.DeliveryTag, false);
                    };
                    await _channel.BasicConsumeAsync($"commands.{_idSensor}", autoAck: false, consumer: consumer);

                    AlterarEstado(true, $"Broker ({_brokerHost})");
                    await Publicar($"HELLO|{_idSensor}|{_zona}|[{_dataTypes}]|{(_videoStream ? "true" : "false")}", $"{_zona}.CONTROL");

                    while (!_encerrando && (_connection?.IsOpen ?? false))
                        await Task.Delay(1000);
                }
                catch (Exception ex)
                {
                    RegistarLog($"Erro broker: {ex.Message}");
                    AlterarEstado(false, "A TENTAR EM 5S...");
                    await Task.Delay(5000);
                }
                finally
                {
                    try { _channel?.Dispose(); _connection?.Dispose(); } catch { }
                    _channel = null; _connection = null;
                }
            }
        }

        static List<SensorConfig> CarregarConfiguracoes()
        {
            string caminho = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\config_sensor.json"));

            if (!File.Exists(caminho))
            {
                var def = new ConfigSensor
                {
                    Leituras = new() { new LeituraConfig { Tipo = "TEMP", IntervaloMs = 5000 } }
                };
                File.WriteAllText(caminho, JsonSerializer.Serialize(def, _jsonWrite));
            }

            var cfg = JsonSerializer.Deserialize<ConfigSensor>(File.ReadAllText(caminho), _jsonRead)!;

            _idSensor    = cfg.SensorId;
            _zona        = cfg.Zona;
            _videoStream = cfg.VideoStream;
            _zonaType    = cfg.ZonaType;
            _brokerHost  = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? cfg.RabbitMqHost;
            _dataTypes   = string.Join(",", cfg.Leituras.ConvertAll(l => l.Tipo.ToUpper()));

            return cfg.Leituras.ConvertAll(l => new SensorConfig
            {
                TipoDado    = l.Tipo.ToUpper(),
                IntervaloMs = l.IntervaloMs
            });
        }

        static void ConfigurarTemporizadores(List<SensorConfig> configs)
        {
            _timerHeartbeat = new Timer(_intervaloHeartbeat);
            _timerHeartbeat.Elapsed += EnviarHeartbeatAutomatico;
            _timerHeartbeat.AutoReset = true;
            _timerHeartbeat.Start();

            foreach (var cfg in configs)
            {
                Timer t = new Timer(cfg.IntervaloMs);
                t.Elapsed += async (_, _) => await GerarEEnviarDado(cfg);
                t.AutoReset = true;
                t.Start();
                _timersDados.Add(t);
            }
        }

        #endregion

        #region BROKER

        static async void EnviarHeartbeatAutomatico(object? sender, ElapsedEventArgs e)
        {
            if (!_isOnline) return;
            await Publicar($"HEARTBEAT|{_idSensor}", $"{_zona}.CONTROL");
        }

        static async Task Publicar(string mensagem, string routingKey)
        {
            if (_channel == null || !_isOnline) return;
            try
            {
                var body = Encoding.UTF8.GetBytes(mensagem);
                await _channel.BasicPublishAsync(EXCHANGE, routingKey, body);
            }
            catch { AlterarEstado(false, "FALHA BROKER"); }
        }

        #endregion

        #region STREAMING

        static void ProcessarComando(string cmd)
        {
            string[] partes = cmd.Split('|');
            for (int i = 0; i < partes.Length; i++)
            {
                if (partes[i] == "STREAM_TO" && i + 1 < partes.Length)
                {
                    string[] ipPort = partes[i + 1].Split(':');
                    if (ipPort.Length == 2 && int.TryParse(ipPort[1], out int port))
                        IniciarStream(ipPort[0], port);
                }
                else if (partes[i] == "STOP_STREAM")
                {
                    PararStream();
                }
            }
        }

        static void IniciarStream(string serverIp, int udpPort)
        {
            if (_streamingAtivo) return;
            _streamingAtivo = true;
            _threadStream   = new Thread(() => StreamarVideo(serverIp, udpPort)) { IsBackground = true, Name = "VideoStream" };
            _threadStream.Start();
            RegistarLog("Stream de vídeo iniciado.");
        }

        static void PararStream()
        {
            _streamingAtivo = false;
            RegistarLog("Stream de vídeo terminado.");
        }

        static void StreamarVideo(string serverIp, int udpPort)
        {
            try
            {
                using var capture = new VideoCapture(0);
                if (!capture.IsOpened())
                {
                    RegistarLog("Câmara não disponível.");
                    _streamingAtivo = false;
                    return;
                }
                capture.Set(VideoCaptureProperties.FrameWidth,  320);
                capture.Set(VideoCaptureProperties.FrameHeight, 240);

                using var udpClient = new UdpClient();
                var endpoint = new IPEndPoint(IPAddress.Parse(serverIp), udpPort);

                using var frame = new Mat();
                while (_streamingAtivo && _isOnline)
                {
                    if (!capture.Read(frame) || frame.Empty()) continue;

                    Cv2.ImEncode(".jpg", frame, out byte[] jpeg,
                        new ImageEncodingParam(ImwriteFlags.JpegQuality, 65));

                    if (jpeg.Length <= 60000)
                        udpClient.Send(jpeg, jpeg.Length, endpoint);

                    Thread.Sleep(33);
                }
            }
            catch (Exception ex) { RegistarLog($"Erro stream: {ex.Message}"); }
            finally { _streamingAtivo = false; }
        }

        #endregion

        #region TUI

        static void AlterarEstado(bool status, string descricao)
        {
            _isOnline    = status;
            _brokerLabel = descricao;
            lock (_consoleLock) { DesenharDashboard(); }
        }

        static void RegistarLog(string mensagem)
        {
            lock (_consoleLock)
            {
                _ultimosLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {mensagem}");
                if (_ultimosLogs.Count > 10) _ultimosLogs.RemoveAt(10);
                DesenharDashboard();
            }
        }

        static void DesenharDashboard()
        {
            try { Console.SetCursorPosition(0, 0); } catch { Console.Clear(); }
            Console.CursorVisible = false;
            string sep = new string('=', 110);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(sep);
            Console.WriteLine("                                             [ ONE HEALTH - SENSOR ]                                             ");
            Console.WriteLine(sep);
            Console.ResetColor();

            Console.Write($"  ID: {_idSensor} | ZONA: {_zona} | VIDEO: ");
            if (_videoStream) { Console.ForegroundColor = ConsoleColor.Magenta; Console.Write("SIM"); }
            else              { Console.ForegroundColor = ConsoleColor.DarkGray; Console.Write("NAO"); }
            Console.ResetColor();
            Console.Write(" | REDE: ");
            if (_isOnline) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine($"ONLINE ({_brokerLabel})".PadRight(62)); }
            else           { Console.ForegroundColor = ConsoleColor.Red;   Console.WriteLine($"OFFLINE / {_brokerLabel}".PadRight(62)); }
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(sep);
            Console.ResetColor();

            Console.WriteLine(new string(' ', 110));
            Console.WriteLine("[ ULTIMAS 10 LEITURAS AMBIENTAIS ENVIADAS ]".PadRight(110));
            for (int i = 0; i < 10; i++)
            {
                if (i < _ultimosLogs.Count)
                {
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine($"   > {_ultimosLogs[i]}".PadRight(110));
                    Console.ResetColor();
                }
                else Console.WriteLine(new string(' ', 110));
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(new string(' ', 110));
            Console.WriteLine(sep);
            Console.ResetColor();
            Console.WriteLine(" Ctrl+C para sair  |  Comandos: evento <tipo> [0.1-2.0],  limpar,  status,  help".PadRight(110));
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.Write("  CMD> ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(_debugInput.ToString().PadRight(103));
            Console.ResetColor();
        }

        #endregion

        #region ENCERRAMENTO

        static void TratarEncerramento(object? sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true;
            _encerrando = true;
            foreach (var t in _timersDados) t.Stop();
            _timerHeartbeat?.Stop();
            if (_isOnline) _ = Publicar($"BYE|{_idSensor}", $"{_zona}.CONTROL");
            AlterarEstado(false, "DESLIGADO");
            Thread.Sleep(500);
            Environment.Exit(0);
        }

        #endregion
    }
}
