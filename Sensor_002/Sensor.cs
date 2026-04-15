using System;
using System.IO;
using System.Net.Sockets;
using System.Timers;
using System.Threading;
using System.Collections.Generic;
using Timer = System.Timers.Timer;

namespace sensor
{
    class SensorConfig
    {
        public string TipoDado { get; set; }
        public int IntervaloMs { get; set; }
        public bool AlarmePossivel { get; set; }
        public double LimiteAlarme { get; set; }
    }

    class Program
    {
        static StreamWriter _writer;
        static StreamReader _reader;
        static readonly object streamLock = new object();

        static string _idSensor = "S002";
        static string _zona = "CHAVES";
        static string _dataTypes = ""; 

        static int _intervaloHeartbeat = 5000;
        static Timer _timerHeartbeat;
        static List<Timer> _timersDados = new List<Timer>();

        private static readonly object _consoleLock = new object();
        private static List<string> _ultimosLogs = new List<string>();
        private static List<string> _ultimosAlarmes = new List<string>();
        
        // CORREÇÃO: O Estado volta a ser um bool real!
        private static bool _isOnline = false;
        private static bool _encerrando = false;
        private static string _gatewayConectado = "";

        static void Main(string[] args)
        {
            Console.CancelKeyPress += new ConsoleCancelEventHandler(TratarEncerramento);

            string ipGateway = args.Length > 0 ? args[0] : "127.0.0.1";
            int portGateway = 5000;

            List<SensorConfig> configs = CarregarConfiguracoes();

            DesenharDashboard();
            ConfigurarTemporizadores(configs); 

            while (!_encerrando)
            {
                try
                {
                    AlterarEstado(false, "A LIGAR...");
                    
                    using (TcpClient client = new TcpClient(ipGateway, portGateway))
                    using (NetworkStream stream = client.GetStream())
                    using (StreamReader reader = new StreamReader(stream))
                    using (StreamWriter writer = new StreamWriter(stream) { AutoFlush = true })
                    {
                        AlterarEstado(true, "Gateway_001");
                        _writer = writer;
                        _reader = reader;

                        EnviarMensagem($"HELLO|{_idSensor}|{_zona}|[{_dataTypes}]");

                        // Fica a rodar enquanto o bool permitir
                        while (_isOnline)
                        {
                            Thread.Sleep(1000);
                        }
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
            string caminhoConfig = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\config_sensor.csv"));
            List<SensorConfig> lista = new List<SensorConfig>();

            if (!File.Exists(caminhoConfig))
            {
                string defaultConfig = 
                    "TEMP;3000;true;45.0\n" +
                    "HUM;5000;false;0.0";
                File.WriteAllText(caminhoConfig, defaultConfig);
            }

            string[] linhas = File.ReadAllLines(caminhoConfig);
            List<string> tiposEncontrados = new List<string>();

            foreach (string linha in linhas)
            {
                if (string.IsNullOrWhiteSpace(linha)) continue;
                string[] col = linha.Split(';');
                if (col.Length == 4)
                {
                    lista.Add(new SensorConfig
                    {
                        TipoDado = col[0].Trim().ToUpper(),
                        IntervaloMs = int.Parse(col[1].Trim()),
                        AlarmePossivel = bool.Parse(col[2].Trim()),
                        LimiteAlarme = double.Parse(col[3].Trim(), System.Globalization.CultureInfo.InvariantCulture)
                    });
                    tiposEncontrados.Add(col[0].Trim().ToUpper());
                }
            }

            _dataTypes = string.Join(",", tiposEncontrados);
            return lista;
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
                t.Elapsed += (sender, e) => GerarEEnviarDado(cfg);
                t.AutoReset = true;
                t.Start();
                _timersDados.Add(t);
            }
        }

        static void GerarEEnviarDado(SensorConfig cfg)
        {
            if (!_isOnline) return;

            Random r = new Random();
            double valorGerado = 0.0;

            if (cfg.TipoDado == "CO2") valorGerado = r.NextDouble() * 550.0;
            else valorGerado = r.NextDouble() * (cfg.LimiteAlarme + 20.0); // Fallback dinâmico

            // Se o alarme estiver ativado na config E o valor aleatório ultrapassar, é anomalia!
            bool isAnomalia = cfg.AlarmePossivel && (valorGerado > cfg.LimiteAlarme);
            
            string timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            string valStr = Math.Round(valorGerado, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (isAnomalia)
            {
                RegistarLog($"ALERTA: Anomalia em {cfg.TipoDado}! ({valStr} excedeu {cfg.LimiteAlarme})", true);
                EnviarMensagem($"ALARM_SEND|{_idSensor}|{cfg.TipoDado}|{valStr}|{timestamp}");
                EnviarMensagem($"VIDEO_REQ|{_idSensor}");
            }
            else
            {
                RegistarLog($"{cfg.TipoDado}: {valStr} recolhido.");
                EnviarMensagem($"DATA_SEND|{_idSensor}|{cfg.TipoDado}|{valStr}|{timestamp}");
            }
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
                    _writer.WriteLine(mensagem);
                    string resposta = _reader.ReadLine();
                    if (resposta == null) AlterarEstado(false, "FALHA REDE");
                }
                catch { AlterarEstado(false, "FALHA REDE"); }
            }
        }

        // ==========================================
        // LÓGICA DO DASHBOARD 
        // ==========================================
        static void AlterarEstado(bool status, string descricao)
        {
            _isOnline = status;
            _gatewayConectado = descricao; // Descrição UI ou GWID
            DesenharDashboard();
        }

        static void RegistarLog(string mensagem, bool isAlarm = false)
        {
            lock (_consoleLock)
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                string linha = $"[{timestamp}] {mensagem}";

                if (isAlarm)
                {
                    _ultimosAlarmes.Insert(0, linha);
                    if (_ultimosAlarmes.Count > 10) _ultimosAlarmes.RemoveAt(10);
                }
                else
                {
                    _ultimosLogs.Insert(0, linha);
                    if (_ultimosLogs.Count > 10) _ultimosLogs.RemoveAt(10);
                }
                DesenharDashboard();
            }
        }

        static void DesenharDashboard()
        {
            Console.Clear();
            string linhaSeparadora = new string('=', 110);
            
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(linhaSeparadora);
            Console.WriteLine("                                             [ ONE HEALTH - SENSOR ]                                             ");
            Console.WriteLine(linhaSeparadora);
            Console.ResetColor();
            
            Console.Write($"  ID: {_idSensor} | ZONA: {_zona} | REDE: ");
            
            if (_isOnline) 
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"ONLINE ({_gatewayConectado})");
            }
            else 
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"OFFLINE / {_gatewayConectado}");
            }
            Console.ResetColor();
            
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(linhaSeparadora);
            Console.ResetColor();
            
            Console.WriteLine("\n[ ÚLTIMOS 10 EVENTOS CRÍTICOS (SIMULAÇÕES) ]");
            if (_ultimosAlarmes.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("   -> Sistema normal.");
                Console.ResetColor();
            }
            else 
            {
                foreach (string a in _ultimosAlarmes) 
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"   !!! {a}");
                    Console.ResetColor();
                }
            }

            Console.WriteLine("\n[ ÚLTIMAS 10 LEITURAS AMBIENTAIS ENVIADAS ]");
            foreach (string l in _ultimosLogs) 
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine($"   > {l}");
                Console.ResetColor();
            }
            
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n" + linhaSeparadora);
            Console.ResetColor();
            Console.WriteLine(" Pressione Ctrl+C para desligar e enviar comando BYE.");
        }

        static void TratarEncerramento(object sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true;
            _encerrando = true;

            foreach(var t in _timersDados) t.Stop();
            _timerHeartbeat?.Stop();
            
            if (_isOnline && _writer != null)
            {
                EnviarMensagem($"BYE|{_idSensor}");
            }
            AlterarEstado(false, "DESLIGADO");
            Thread.Sleep(500); 
            Environment.Exit(0); 
        }
    }
}