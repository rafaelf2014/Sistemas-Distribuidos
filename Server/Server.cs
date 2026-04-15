using System;
using System.IO;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;

class ServerCentral
{
    private static readonly object _dbLock = new object();
    private static readonly string dbPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\ServerData.db"));
    private static readonly string connectionString = $"Data Source={dbPath}";
    static TcpListener _server = null;

    private static readonly object _consoleLock = new object();
    private static List<string> _ultimosLogs = new List<string>();
    private static List<string> _ultimosAlarmes = new List<string>();
    private static bool _isOnline = true;

    public static void Main()
    {
        Console.CancelKeyPress += new ConsoleCancelEventHandler(TratarEncerramento);
        InicializarBaseDeDados();
        try
        {
            Int32 port = 14000;
            IPAddress localAddr = IPAddress.Parse("127.0.0.1");
            _server = new TcpListener(localAddr, port);
            _server.Start();

            RegistarLog($"Servidor à escuta na porta {port}...");
            RegistarLog($"Ficheiro DB ativo em: {dbPath}");

            while (true)
            {
                TcpClient client = _server.AcceptTcpClient();
                Thread clientThread = new Thread(() => HandleGateway(client));
                clientThread.Start();
            }
        }
        catch (SocketException) { RegistarLog("Escuta interrompida."); }
        finally { _server?.Stop(); }
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

    // ==========================================
    // LÓGICA DO DASHBOARD COM CORES (SERVIDOR)
    // ==========================================
    static void DesenharDashboard()
    {
        Console.Clear();
        string linhaSeparadora = new string('=', 110);
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(linhaSeparadora);
        Console.WriteLine("                                          [ ONE HEALTH - SERVIDOR CENTRAL ]                                      ");
        Console.WriteLine(linhaSeparadora);
        Console.ResetColor();
        Console.Write("  ESTADO: ");
        if (_isOnline) { Console.ForegroundColor = ConsoleColor.Green; Console.Write("ONLINE"); }
        else { Console.ForegroundColor = ConsoleColor.Red; Console.Write("OFFLINE"); }
        Console.ResetColor();
        Console.WriteLine("   |   PROTOCOLO: TCP (STATEFUL)         ");

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(linhaSeparadora);
        Console.ResetColor();

        Console.WriteLine("\n[ ÚLTIMOS 10 ALARMES URGENTES (EDGE ANALYTICS) ]");
        if (_ultimosAlarmes.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("   -> Sem anomalias registadas.");
            Console.ResetColor();
        }
        else
        {
            foreach (string alarme in _ultimosAlarmes)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("   !!! ");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(alarme);
                Console.ResetColor();
            }
        }

        Console.WriteLine("\n[ ÚLTIMAS 10 ATIVIDADES (TRÁFEGO NORMAL) ]");
        foreach (string log in _ultimosLogs)
        {
            if (log.Contains("Erro") || log.Contains("ERRO")) Console.ForegroundColor = ConsoleColor.Red;
            else Console.ForegroundColor = ConsoleColor.White;

            Console.WriteLine($"   > {log}");
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n" + linhaSeparadora);
        Console.ResetColor();
        Console.WriteLine(" Pressione Ctrl+C para encerrar o servidor de forma limpa.");
    }

    static void HandleGateway(TcpClient gatewayClient)
    {
        string endpoint = gatewayClient.Client.RemoteEndPoint.ToString();
        try
        {
            using NetworkStream stream = gatewayClient.GetStream();
            using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
            using StreamWriter writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            string linha;
            while ((linha = reader.ReadLine()) != null)
            {
                string[] partes = linha.Split('|');
                for (int i = 0; i < partes.Length; i++) { partes[i] = partes[i].Trim(); }

                if (partes.Length == 7 && (partes[0] == "DATA_FORWARD" || partes[0] == "ALARM_FORWARD"))
                {
                    bool isAlarm = partes[0] == "ALARM_FORWARD";
                    bool inserido = InserirNaBaseDeDados(partes[1], partes[2], partes[3], partes[4], partes[5], partes[6], isAlarm);

                    if (inserido)
                    {
                        writer.WriteLine("ACK_FORWARDDATA|STATUS OK");
                        if (isAlarm) RegistarLog($"[{partes[3]}] ANOMALIA! Sensor: {partes[2]} | {partes[4]} = {partes[5]}", true);
                        else RegistarLog($"[{partes[3]}] {partes[2]} -> {partes[4]} = {partes[5]}");
                    }
                    else writer.WriteLine("ACK_FORWARDDATA|ERRO DB");
                }
                else writer.WriteLine("ACK_FORWARDDATA|ERRO FORMATO");
            }
        }
        catch (Exception e) { RegistarLog($"ERRO REDE ({endpoint}): {e.Message}"); }
        finally { gatewayClient.Close(); }
    }
    static void InicializarBaseDeDados()
    {
        lock (_dbLock)
        {
            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    var cmd = connection.CreateCommand();
                    //Adicionasse o UNIQUE(SensorId, TipoDado, Timestamp) para não guardarmos dados duplicados
                    cmd.CommandText = @"
                    CREATE TABLE 
                    IF NOT EXISTS Dados (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT, 
                    GatewayId TEXT, 
                    SensorId TEXT, 
                    Zona TEXT, 
                    TipoDado TEXT, 
                    Valor TEXT, 
                    Timestamp TEXT, 
                    IsAlarm INTEGER DEFAULT 0,
                    UNIQUE(SensorId, TipoDado, Timestamp)   
                    )";
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex) { RegistarLog($"Erro DB: {ex.Message}"); }
        }
    }

    static bool InserirNaBaseDeDados(string gatewayId, string sensorId, string zona, string tipoDado, string valor, string timestamp, bool isAlarm)
    {
        lock (_dbLock)
        {
            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    var cmd = connection.CreateCommand();
                    // Adicionamos o Ignore para o caso dos dados não serem únicos nós não os inserimos na base de dados
                    cmd.CommandText = @"
                    INSERT OR IGNORE INTO Dados (
                    GatewayId, 
                    SensorId, 
                    Zona, 
                    TipoDado, 
                    Valor, 
                    Timestamp, 
                    IsAlarm
                    ) 
                    VALUES (
                    $gatewayId, 
                    $sensorId, 
                    $zona, 
                    $tipoDado, 
                    $valor, 
                    $timestamp, 
                    $isAlarm
                    )";
                    cmd.Parameters.AddWithValue("$gatewayId", gatewayId);
                    cmd.Parameters.AddWithValue("$sensorId", sensorId);
                    cmd.Parameters.AddWithValue("$zona", zona);
                    cmd.Parameters.AddWithValue("$tipoDado", tipoDado);
                    cmd.Parameters.AddWithValue("$valor", valor);
                    cmd.Parameters.AddWithValue("$timestamp", timestamp);
                    cmd.Parameters.AddWithValue("$isAlarm", isAlarm ? 1 : 0);
                    cmd.ExecuteNonQuery(); //Remove-se a linha return cmd.ExecuteNonQuery() > 0, porque o comando ExecuteNonQuery retorna o número de linhas inseridas. Como podem haver dados duplicados que são ignorados, o comando iria retornar 0, o que faria com que fosse retornado False para o gateway, o que geraria um loop de envio dos mesmos dados.
                    return true;
                }
            }
            catch (Exception ex) { RegistarLog($"Erro INSERT DB: {ex.Message}"); return false; }
        }
    }

    static void TratarEncerramento(object sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        _isOnline = false;
        RegistarLog("A desligar o Servidor e a fechar as portas de forma limpa...");
        _server?.Stop();
        Thread.Sleep(500);
        Environment.Exit(0);
    }
}