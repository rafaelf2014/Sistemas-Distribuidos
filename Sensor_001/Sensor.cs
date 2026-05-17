using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Timers;
using System.Threading;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;
using Timer = System.Timers.Timer;
using Amqp;
using Amqp.Framing;
using System.ComponentModel;

namespace sensor
{
    #region CONFIG (DTOs)

    class LeituraConfig
    {
        [JsonPropertyName("tipo")] public string Tipo { get; set; }
        [JsonPropertyName("intervaloMs")] public int IntervaloMs { get; set; }
    }

    class ConfigSensor
    {
        [JsonPropertyName("sensorId")] public string SensorId { get; set; } = "S???";
        [JsonPropertyName("zona")] public string Zona { get; set; } = "DESCONHECIDA";
        [JsonPropertyName("videoStream")] public bool VideoStream { get; set; } = false;
        [JsonPropertyName("leituras")] public List<LeituraConfig> Leituras { get; set; } = new();
    }

    class SensorConfig
    {
        public string TipoDado { get; set; }
        public int IntervaloMs { get; set; }
    }

    #endregion

    class Program
    {
        #region CAMPOS

        static string _idSensor = "S???";
        static string _zona = "DESCONHECIDA";
        static bool _videoStream = false;
        static string _dataTypes = "";

        static Timer _timerHeartbeat;
        static readonly List<Timer> _timersDados = new();

        static readonly int _intervaloHeartbeat = 5000;

        private static readonly object _consoleLock = new object();
        private static readonly List<string> _ultimosLogs = new();
        private static readonly Random _rng = new();

        private static readonly JsonSerializerOptions _jsonRead = new() { PropertyNameCaseInsensitive = true };
        private static readonly JsonSerializerOptions _jsonWrite = new() { WriteIndented = true };

        private static bool _isOnline = false;
        private static bool _encerrando = false;
        private static string _statusConexao = ""; //Vai se conectar ao broker, devo remover?

        private static volatile bool _streamingAtivo = false;
        private static Thread _threadStream = null;

        // Campos Publisher-Subscriber AMQP Connection
        private static Connection _connection;  //Conexão com o brooker
        private static Session _session;    //Sessão de comunicação
        private static SenderLink _sender;  //Interface do publicador
        private static ReceiverLink _receiver;  //Interface do recetor

        #endregion

        #region INICIALIZAÇÃO

        static void Main(string[] args)
        {
            Console.CancelKeyPress += TratarEncerramento;
            string ipGateway = args.Length > 0 ? args[0] : "127.0.0.1";

            List<SensorConfig> configs = CarregarConfiguracoes();
            DesenharDashboard();
            ConfigurarTemporizadores(configs);

            while (!_encerrando)
            {
                try //Trocar para publisher-Subscriber
                {
                    AlterarEstado(false, "A ligar AMQP...");

                    Address address = new Address($"amqp://guest:guest@{ipGateway}:5672");  //Endereço do brooker

                    // 1. Criar conexão e escutar motivo de fecho
                    _connection = new Connection(address);
                    _connection.Closed += (sender, error) => RegistarLog($"[BROKER FECHOU A CONEXÃO] {error?.Condition}: {error?.Description}");

                    _session = new Session(_connection);

                    // 2. Criar Sender e escutar motivo
                    _sender = new SenderLink(_session, "sensor-sender", $"/queues/sensor.telemetry.{_zona.Trim()}");
                    _sender.Closed += (sender, error) => RegistarLog($"[BROKER REJEITOU SENDER] {error?.Condition}: {error?.Description}");

                    // 3. Criar Receiver e escutar motivo
                    _receiver = new ReceiverLink(_session, "sensor-receiver", $"/queues/sensor.commands.{_idSensor.Trim()}");
                    _receiver.Closed += (sender, error) => RegistarLog($"[BROKER REJEITOU RECEIVER] {error?.Condition}: {error?.Description}");

                    AlterarEstado(true, "Gateway/Broker");

                    _receiver.Start(10, ProcessarComandoAmqp);  // Inicializa escuta assíncrona de dados, colocamos um limite de 10 mensagens que podem correr em segundo plano sem necessidade de enviar ACK.

                    EnviarMensagem($"HELLO|{_idSensor}|{_zona}|[{_dataTypes}]|{(_videoStream ? "true" : "false")}");

                    while (!_encerrando)
                    {
                        if (_connection == null || _connection.IsClosed)
                            throw new Exception("Ligação AMQP foi cortada em background.");

                        Thread.Sleep(1000);
                    }
                }
                catch (Exception ex)
                {
                    RegistarLog($"Erro: {ex.Message}");
                    AlterarEstado(false, "A TENTAR EM 5S...");
                    Thread.Sleep(5000);
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

            _idSensor = cfg.SensorId;
            _zona = cfg.Zona;
            _videoStream = cfg.VideoStream;
            _dataTypes = string.Join(",", cfg.Leituras.ConvertAll(l => l.Tipo.ToUpper()));

            return cfg.Leituras.ConvertAll(l => new SensorConfig
            {
                TipoDado = l.Tipo.ToUpper(),
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
                t.Elapsed += (_, _) => GerarEEnviarDado(cfg);
                t.AutoReset = true;
                t.Start();
                _timersDados.Add(t);
            }
        }

        #endregion

        #region DADOS & HEARTBEAT

        static void GerarEEnviarDado(SensorConfig cfg)
        {
            if (!_isOnline) return;

            double v = cfg.TipoDado switch
            {
                "TEMP" => _rng.NextDouble() * 50.0,
                "HUM" => _rng.NextDouble() * 100.0,
                _ => _rng.NextDouble() * 100.0
            };

            // 10%
            if (_rng.Next(10) == 0) v += 60.0;

            string ts = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            string val = Math.Round(v, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

            RegistarLog($"{cfg.TipoDado}: {val} recolhido.");
            EnviarMensagem($"DATA_SEND|{_idSensor}|{cfg.TipoDado}|{val}|{ts}");
        }

        static void EnviarHeartbeatAutomatico(object sender, ElapsedEventArgs e)
        {
            if (!_isOnline) return;
            EnviarMensagem($"HEARTBEAT|{_idSensor}");
        }

        static void EnviarMensagem(string mensagem)
        {
            try
            {
                if (_sender != null && _sender.IsClosed)
                {
                    // Extrair o motivo exato pelo qual o RabbitMQ cortou o acesso
                    string condicao = _sender.Error?.Condition?.ToString() ?? "N/A";
                    string descricao = _sender.Error?.Description ?? "Fila não encontrada ou acesso negado.";

                    RegistarLog($"[AMQP ERROR] Broker rejeitou: {condicao} -> {descricao}");

                    _connection?.Close(); // Força o ciclo principal a cair no catch
                    return;
                }

                if (_sender == null) return;

                Message msg = new Message(mensagem);
                msg.Properties = new Properties() { Subject = "telemetry", MessageId = Guid.NewGuid().ToString() };

                _sender.Send(msg, TimeSpan.FromSeconds(2));
            }
            catch
            {
                AlterarEstado(false, "FALHA DE REDE!");
                _connection?.Close();
            }
        }

        #endregion

        #region STREAMING

        // Analisa o ACK do gateway e activa/desactiva o stream se vier um comando, é pra isto que ser o hijack no gateway
        static void ProcessarComandoAmqp(IReceiverLink receiver, Message message)
        {
            try
            {
                // Avisa o Broker que a mensagem foi recebida com sucesso
                receiver.Accept(message);

                string comando = message.Body?.ToString();
                if (string.IsNullOrEmpty(comando)) return;

                string[] partes = comando.Split('|');

                // Tratamento de Comandos
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
                    else if (partes[i] == "REQUEST_HELLO")
                    {
                        RegistarLog("Gateway solicitou reidentificação. A enviar HELLO...");
                        EnviarMensagem($"HELLO|{_idSensor}|{_zona}|[{_dataTypes}]|{(_videoStream ? "true" : "false")}");
                    }
                }
            }
            catch (Exception ex)
            {
                RegistarLog($"Erro ao processar comando: {ex.Message}");
                receiver.Reject(message);
            }
        }

        static void IniciarStream(string serverIp, int udpPort)
        {
            if (_streamingAtivo) return;
            _streamingAtivo = true;
            _threadStream = new Thread(() => StreamarVideo(serverIp, udpPort)) { IsBackground = true, Name = "VideoStream" };
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
                using var capture = new VideoCapture(0); // câmara índice 0
                if (!capture.IsOpened())
                {
                    RegistarLog("Câmara não disponível.");
                    _streamingAtivo = false;
                    return;
                }
                capture.Set(VideoCaptureProperties.FrameWidth, 320);
                capture.Set(VideoCaptureProperties.FrameHeight, 240);

                using var udpClient = new UdpClient();
                var endpoint = new IPEndPoint(IPAddress.Parse(serverIp), udpPort);

                using var frame = new Mat();
                while (_streamingAtivo && _isOnline)
                {
                    if (!capture.Read(frame) || frame.Empty()) continue;

                    // diminuir tamanho dos frames
                    Cv2.ImEncode(".jpg", frame, out byte[] jpeg,
                        new ImageEncodingParam(ImwriteFlags.JpegQuality, 65));

                    if (jpeg.Length <= 60000)
                        udpClient.Send(jpeg, jpeg.Length, endpoint);

                    Thread.Sleep(33); // ~30 fps
                }
            }
            catch (Exception ex) { RegistarLog($"Erro stream: {ex.Message}"); }
            finally { _streamingAtivo = false; }
        }

        #endregion

        #region TUI

        static void AlterarEstado(bool status, string descricao)
        {
            _isOnline = status;
            _statusConexao = descricao;
            DesenharDashboard();
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
            else { Console.ForegroundColor = ConsoleColor.DarkGray; Console.Write("NAO"); }
            Console.ResetColor();
            Console.Write(" | REDE: ");
            if (_isOnline) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine($"ONLINE ({_statusConexao})".PadRight(62)); }
            else { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"OFFLINE / {_statusConexao}".PadRight(62)); }
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
            Console.WriteLine(" Pressione Ctrl+C para desligar.".PadRight(110));
        }

        #endregion

        #region ENCERRAMENTO

        static void TratarEncerramento(object sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true;
            _encerrando = true;
            foreach (var t in _timersDados) t.Stop();
            _timerHeartbeat?.Stop();
            if (_isOnline) EnviarMensagem($"BYE|{_idSensor}");
            AlterarEstado(false, "DESLIGADO");
            Thread.Sleep(500);
            Environment.Exit(0);
        }

        #endregion
    }
}
