using System;
using System.IO;
using System.Net.Sockets;
using System.Timers;
using System.Threading;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Timer = System.Timers.Timer;

namespace sensor
{
    #region CONFIG (DTOs)

    class LeituraConfig
    {
        [JsonPropertyName("tipo")]        public string Tipo        { get; set; }
        [JsonPropertyName("intervaloMs")] public int    IntervaloMs { get; set; }
    }

    class ConfigSensor
    {
        [JsonPropertyName("sensorId")]    public string              SensorId    { get; set; } = "S???";
        [JsonPropertyName("zona")]        public string              Zona        { get; set; } = "DESCONHECIDA";
        [JsonPropertyName("videoStream")] public bool                VideoStream { get; set; } = false;
        [JsonPropertyName("leituras")]    public List<LeituraConfig> Leituras    { get; set; } = new();
    }

    class SensorConfig
    {
        public string TipoDado    { get; set; }
        public int    IntervaloMs { get; set; }
    }

    #endregion

    class Program
    {
        #region CAMPOS

        static StreamWriter _writer;
        static StreamReader _reader;
        static readonly object streamLock = new object();

        static string _idSensor    = "S???";
        static string _zona        = "DESCONHECIDA";
        static bool   _videoStream = false;
        static string _dataTypes   = "";

        static Timer _timerHeartbeat;
        static readonly List<Timer> _timersDados       = new();
        static readonly int         _intervaloHeartbeat = 5000;

        private static readonly object       _consoleLock = new object();
        private static readonly List<string> _ultimosLogs = new();
        private static readonly Random       _rng         = new();

        private static readonly JsonSerializerOptions _jsonRead  = new() { PropertyNameCaseInsensitive = true };
        private static readonly JsonSerializerOptions _jsonWrite = new() { WriteIndented = true };

        private static bool   _isOnline        = false;
        private static bool   _encerrando      = false;
        private static string _gatewayConectado = "";

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
                try
                {
                    AlterarEstado(false, "A LIGAR...");

                    using (TcpClient client = new TcpClient(ipGateway, 5000))
                    using (NetworkStream stream = client.GetStream())
                    using (StreamReader reader = new StreamReader(stream))
                    using (StreamWriter writer = new StreamWriter(stream) { AutoFlush = true })
                    {
                        lock (streamLock) { _writer = writer; _reader = reader; }
                        AlterarEstado(true, "Gateway");

                        EnviarMensagem($"HELLO|{_idSensor}|{_zona}|[{_dataTypes}]|{(_videoStream ? "true" : "false")}");

                        while (_isOnline) Thread.Sleep(1000);
                    }
                }
                catch (Exception)
                {
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

            _idSensor    = cfg.SensorId;
            _zona        = cfg.Zona;
            _videoStream = cfg.VideoStream;
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
                "TEMP"  => _rng.NextDouble() * 50.0,
                "HUM"   => _rng.NextDouble() * 100.0,
                "CO2"   => _rng.NextDouble() * 550.0,
                "RUIDO" => _rng.NextDouble() * 120.0,
                _       => _rng.NextDouble() * 100.0
            };

            // 10%
            if (_rng.Next(10) == 0) v += 60.0;

            string ts  = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
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
            lock (streamLock)
            {
                try
                {
                    if (_writer == null) return;
                    _writer.WriteLine(mensagem);
                    string resposta = _reader.ReadLine();
                    if (resposta == null) AlterarEstado(false, "FALHA REDE");
                }
                catch { AlterarEstado(false, "FALHA REDE"); }
            }
        }

        #endregion

        #region TUI

        static void AlterarEstado(bool status, string descricao)
        {
            _isOnline        = status;
            _gatewayConectado = descricao;
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
            else              { Console.ForegroundColor = ConsoleColor.DarkGray; Console.Write("NAO"); }
            Console.ResetColor();
            Console.Write(" | REDE: ");
            if (_isOnline) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine($"ONLINE ({_gatewayConectado})".PadRight(62)); }
            else           { Console.ForegroundColor = ConsoleColor.Red;   Console.WriteLine($"OFFLINE / {_gatewayConectado}".PadRight(62)); }
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
            if (_isOnline && _writer != null) EnviarMensagem($"BYE|{_idSensor}");
            AlterarEstado(false, "DESLIGADO");
            Thread.Sleep(500);
            Environment.Exit(0);
        }

        #endregion
    }
}
