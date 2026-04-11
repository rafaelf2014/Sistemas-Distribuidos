using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Timers;
using Timer = System.Timers.Timer;
using System.Globalization;

namespace sensor
{
    class Program
    {
        static StreamWriter _writer;
        static StreamReader _reader;
        static readonly object streamLock = new object();

        static System.Timers.Timer _timerTemperatura;
        static System.Timers.Timer _timerHeartbeat;
        static bool _conectado = false;

        // Mete aqui os dados do sensor (id, zona e tipo de dados)
        static string _idSensor = "S001";
        static string _zona = "CHAVES";
        static string _dataTypes = "TEMP,HUM";

        static int _intervaloDados = 5000; // 1000 = 1 segundo
        static int _intervaloHeartbeat = 3000;

        static void Main(string[] args)
        {
            Console.CancelKeyPress += new ConsoleCancelEventHandler(TratarEncerramento);

            string ipGateway = args.Length > 0 ? args[0] : "127.0.0.1";
            int portGateway = 5000;

            Console.WriteLine($"[SENSOR {_idSensor}] ligado na zona: {_zona}...");
            ConfigurarTemporizadores();

            while (true)
            {
                try
                {
                    if (!_conectado) {
                        Console.WriteLine($"\n[SENSOR {_idSensor}] a tentar ligar ao Gateway em {ipGateway}:{portGateway}..."); }
                    
                    using (TcpClient client = new TcpClient(ipGateway, portGateway))
                    using (NetworkStream stream = client.GetStream())
                    using (StreamReader reader = new StreamReader(stream))
                    using (StreamWriter writer = new StreamWriter(stream) { AutoFlush = true })
                    {
                        Console.WriteLine($"[SENSOR {_idSensor}] ligado com sucesso ao Gateway!");
                        _writer = writer;
                        _reader = reader;
                        _conectado = true;

                        EnviarMensagem($"HELLO|{_idSensor}|{_zona}|[{_dataTypes}]");

                        while (_conectado)
                        {
                            Thread.Sleep(1000);
                        }
                        
                    }
                }
                catch (SocketException)
                {
                    _conectado = false;
                    Console.WriteLine("\n[ERRO] Gateway não alcançado. A tentar novamente em 5 segundos...");
                    Thread.Sleep(5000);
                }
                catch (Exception ex)
                {
                    _conectado = false;
                    Console.WriteLine($"\n[Exception]: {ex.Message}");
                    Console.WriteLine("A tentar novamente em 5 segundos...");
                    Thread.Sleep(5000);
                }
            }
        }

        static void TratarEncerramento(object sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true; 
            
            Console.WriteLine("\n\n[AVISO] A encerrar o sensor...");

            if (_conectado && _writer != null)
            {
                EnviarMensagem($"BYE|{_idSensor}");
            }

            Console.WriteLine("Sensor desligado. XAU!");
            Thread.Sleep(500); 
            Environment.Exit(0); 
        }

        static void ConfigurarTemporizadores()
        {
            _timerTemperatura = new Timer(_intervaloDados);
            _timerTemperatura.Elapsed += EnviarDadosAutomaticos;
            _timerTemperatura.AutoReset = true;
            _timerTemperatura.Start();

            _timerHeartbeat = new Timer(_intervaloHeartbeat);
            _timerHeartbeat.Elapsed += EnviarHeartbeatAutomatico;
            _timerHeartbeat.AutoReset = true;
            _timerHeartbeat.Start();
        }

        static void EnviarDadosAutomaticos(object sender, ElapsedEventArgs e)
        {
            if (!_conectado) return;

            double tempRandom = 15.0 + new Random().NextDouble() * 20.0;
            double humRandom = 30.0 + new Random().NextDouble() * 50.0;
            string timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

            string tempStr = Math.Round(tempRandom, 1).ToString(CultureInfo.InvariantCulture);
            string humStr = Math.Round(humRandom, 1).ToString(CultureInfo.InvariantCulture);

            string msgTemp = $"DATA_SEND|{_idSensor}|TEMP|{tempStr}|{timestamp}";
            EnviarMensagem(msgTemp);
            string msgHum = $"DATA_SEND|{_idSensor}|HUM|{humStr}|{timestamp}";
            EnviarMensagem(msgHum);
        }

        static void EnviarHeartbeatAutomatico(object sender, ElapsedEventArgs e)
        {
            if (!_conectado) return;

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

                    if (resposta == null)
                    {
                        _conectado = false;
                        Console.WriteLine("\n[ERRO] Conexão perdida com o Gateway.");
                        return;
                    }
                    Console.WriteLine($"[TX] {mensagem}");
                    Console.WriteLine($"[RX] {resposta}\n");
                }
                catch
                {
                    _conectado = false;
                }
            }
        }
    }
}