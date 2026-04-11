using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using System.Globalization;
using System.Threading;
using Timer = System.Timers.Timer;

class MyTcpListener
{
    private static string _gatewayId = "Gateway_001";

    static readonly string pastaProjeto = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\"));
    static readonly string caminhoFicheiro = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\sensores.csv"));

    static readonly object fileLock = new object();
    static readonly object _bufferFileLock = new object();
    static Timer _timerAgregacao;

    static TcpListener server = null;

    public static void Main()
    {
        Console.CancelKeyPress += new ConsoleCancelEventHandler(TratarEncerramento);

        Console.WriteLine($"[GATEWAY {_gatewayId}] a iniciar...");
        InicializarFicheiroConfiguracao();

        _timerAgregacao = new Timer(60000); // 5 minutos = 300000
        _timerAgregacao.Elapsed += ProcessarEEnviarAgregados;
        _timerAgregacao.AutoReset = true;
        _timerAgregacao.Start();

        try
        {
            Int32 port = 5000;
            IPAddress localAddr = IPAddress.Parse("127.0.0.1"); 
            server = new TcpListener(localAddr, port);
            server.Start();

            while (true)
            {
                Console.WriteLine("\nA aguardar sensores na porta 5000...");
                TcpClient client = server.AcceptTcpClient();

                Thread threadSensor = new Thread(() => HandleSensor(client));
                threadSensor.Start();
            }
        }
        catch (SocketException e) 
        { 
            Console.WriteLine($"Escuta interrompida: {e.Message}"); 
        }
        finally { server?.Stop(); }
    }

    static void TratarEncerramento(object sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true; 
        Console.WriteLine("\n\n[AVISO] A encerrar o Gateway...");
        
        _timerAgregacao?.Stop();
        server?.Stop();

        Console.WriteLine("Gateway desligado. Os dados em buffer guardados.");
        Thread.Sleep(500);
        Environment.Exit(0);
    }

    static void HandleSensor(TcpClient client)
    {
        string endpoint = client.Client.RemoteEndPoint.ToString();
        Console.WriteLine($"Sensor conectado: {endpoint}");

        try
        {
            using (NetworkStream stream = client.GetStream())
            using (StreamReader reader = new StreamReader(stream))
            using (StreamWriter writer = new StreamWriter(stream) { AutoFlush = true })
            {
                string rawData;
                while ((rawData = reader.ReadLine()) != null)
                {
                    Console.WriteLine($"[RECEBIDO] {rawData}");
                    string[] parts = rawData.Split('|');
                    string command = parts[0].ToUpper();
                    string resposta = "ACK_OK";

                    switch (command)
                    {
                        case "HELLO":
                            if (parts.Length >= 4)
                            {
                                string id = parts[1];
                                string zona = parts[2];
                                string tipos = parts[3];

                                RegistarOuAtualizarSensor(id, zona, tipos);
                            }
                            resposta = "ACK_HELLO|OK";
                            break;

                        case "DATA_SEND":
                            string idData = parts[1];
                            string tipoDado = parts[2];

                            if (double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out double valor))
                            {
                                if (ValidarSensor(idData, tipoDado))
                                {
                                    string nomeFicheiro = $"buffer_{idData}_{tipoDado}.csv";
                                    string caminhoBuffer = Path.Combine(pastaProjeto, nomeFicheiro);
                                    
                                    lock (_bufferFileLock)
                                    {
                                        File.AppendAllText(caminhoBuffer, valor.ToString(CultureInfo.InvariantCulture) + Environment.NewLine);
                                    }
                                    resposta = "ACK_DATA|OK";
                                }
                                else resposta = "ACK_DATA|ERRO_VALIDACAO";
                            }
                            break;

                        case "HEARTBEAT":
                            atualizarLastSync(parts[1]);
                            resposta = "ACK_HEARTBEAT|OK";
                            break;

                        case "BYE":
                            if (parts.Length >= 2) AtualizarEstadoSensor(parts[1], "desativado");
                            resposta = "ACK_BYE|OK";
                            break;

                        default:
                            resposta = "ACK_ERR|Comando Desconecido";
                            break;
                    }
                    writer.WriteLine(resposta);
                    if (command == "BYE") break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Caiu a ligação {endpoint}: {ex.Message}");
        }
        finally
        {
            client.Close();
            Console.WriteLine($"Sensor em {endpoint} desconectado.");
        }
    }

    static void InicializarFicheiroConfiguracao()
    {        
        lock (fileLock)
        {
            if (!File.Exists(caminhoFicheiro))
            {
                File.WriteAllText(caminhoFicheiro, "");
                Console.WriteLine("Ficheiro de Config criado: " + caminhoFicheiro);
            }
        }
    }

    static void RegistarOuAtualizarSensor(string id, string zona, string tipos)
    {
        lock (fileLock)
        {
            if (!File.Exists(caminhoFicheiro)) return;
            var linhas = File.ReadAllLines(caminhoFicheiro).ToList();
            bool encontrado = false;
            string timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

            for (int i = 0; i < linhas.Count; i++)
            {
                var col = linhas[i].Split(';');
                if (col.Length >= 5 && col[0].Trim() == id.Trim())
                {
                    col[1] = "ativo";
                    col[2] = zona;
                    col[3] = tipos;
                    col[4] = timestamp;
                    linhas[i] = string.Join(";", col);
                    encontrado = true;
                    Console.WriteLine($"Sensor {id} atualizado.");
                    break;
                }
            }
            if (!encontrado)
            {
                string novaLinha = $"{id};ativo;{zona};{tipos};{timestamp}";
                linhas.Add(novaLinha);
                Console.WriteLine($"Sensor {id} registado.");
            }

            File.WriteAllLines(caminhoFicheiro, linhas);
        }
    }

    static bool ValidarSensor(string id, string tipoDados)
    {
        lock (fileLock)
        {
            if (!File.Exists(caminhoFicheiro)) return false;
            return File.ReadAllLines(caminhoFicheiro)
                       .Select(l => l.Split(';'))
                       .Any(c => c.Length >= 5 && c[0] == id && c[1] == "ativo" && c[3].Contains(tipoDados));
        }
    }

    static void atualizarLastSync(string id)
    {
        lock (fileLock)
        {
            if (!File.Exists(caminhoFicheiro)) return;
            var linhas = File.ReadAllLines(caminhoFicheiro);
            for (int i = 0; i < linhas.Length; i++)
            {
                var col = linhas[i].Split(';');
                if (col.Length >= 5 && col[0].Trim() == id.Trim())
                {
                    col[4] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                    linhas[i] = string.Join(";", col);
                    File.WriteAllLines(caminhoFicheiro, linhas);
                    break;
                }
            }
        }
    }

    static void AtualizarEstadoSensor(string id, string estado)
    {
        lock (fileLock)
        {
            if (!File.Exists(caminhoFicheiro)) return;
            var linhas = File.ReadAllLines(caminhoFicheiro);
            for (int i = 0; i < linhas.Length; i++)
            {
                var col = linhas[i].Split(';');
                if (col.Length >= 5 && col[0].Trim() == id.Trim())
                {
                    col[1] = estado;
                    linhas[i] = string.Join(";", col);
                    File.WriteAllLines(caminhoFicheiro, linhas);
                    break;
                }
            }
        }
    }

    static string ObterZonaDoSensor(string id)
    {
        lock (fileLock)
        {
            if (!File.Exists(caminhoFicheiro)) return "ZONA DESCONHECIDA";
            
            var linhas = File.ReadAllLines(caminhoFicheiro);
            foreach (var linha in linhas)
            {
                var col = linha.Split(';');
                if (col.Length >= 5 && col[0].Trim() == id.Trim())
                {
                    return col[2];
                }
            }
        }
        return "ZONA DESCONHECIDA";
    }

    static void ProcessarEEnviarAgregados(object sender, ElapsedEventArgs e)
    {
        List<string> mensagensPraEnviar = new List<string>();

        lock (_bufferFileLock)
        {
            string[] ficheirosBuffer = Directory.GetFiles(pastaProjeto, "buffer_*.csv");

            foreach (string ficheiro in ficheirosBuffer)
            {
                try
                {
                    string nome = Path.GetFileNameWithoutExtension(ficheiro);
                    string[] partes = nome.Split('_');
                    if (partes.Length != 3) continue;

                    string sensorId = partes[1];
                    string tipoDado = partes[2];

                    string[] linhas = File.ReadAllLines(ficheiro);
                    if (linhas.Length == 0) continue;

                    List<double> valores = new List<double>();
                    foreach (string linha in linhas)
                    {
                        if (double.TryParse(linha, NumberStyles.Any, CultureInfo.InvariantCulture, out double val))
                        {
                            valores.Add(val);
                        }
                    }

                    if (valores.Count > 0)
                    {
                        double media = valores.Average();
                        string timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

                        string msg = $"DATA_SEND|{sensorId}|{tipoDado}|{media.ToString("F2", CultureInfo.InvariantCulture)}|{timestamp}";
                        mensagensPraEnviar.Add(msg);
                    }

                    File.Delete(ficheiro);
                    Console.WriteLine($"{valores.Count} valores de {sensorId} ({tipoDado}). Ficheiro apagado.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Erro de ficheiro {ficheiro}: {ex.Message}");
                }
            }
        }
        foreach (string msg in mensagensPraEnviar)
        {
            EnviarParaServidor(msg);
        }
    }

    static void EnviarParaServidor(string data)
    {
        try
        {
            using (TcpClient sc = new TcpClient("127.0.0.1", 14000))
            using (NetworkStream s = sc.GetStream())
            using (StreamReader r = new StreamReader(s))
            using (StreamWriter w = new StreamWriter(s) { AutoFlush = true })
            {
                string[] p = data.Split('|');
                string sensorId = p[1];
                string zona = ObterZonaDoSensor(sensorId);

                string modified = $"DATA_FORWARD|{_gatewayId}|{p[1]}|{zona}|{p[2]}|{p[3]}|{p[4]}";
                w.WriteLine(modified);
                Console.WriteLine($"[GATEWAY -> SERVER]: {r.ReadLine()}");
            }
        }
        catch (Exception ex) { Console.WriteLine($"Erro: {ex.Message}"); }
    }
}