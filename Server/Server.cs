using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;

partial class ServerCentral
{
    #region CAMPOS

    private record struct DataRecord(
        string GatewayId, string SensorId, string Zona,
        string TipoDado, string Valor, string Timestamp, bool IsAlarm);

    // Limite de 1000
    private static readonly BlockingCollection<DataRecord> _filaEscrita = new(1000);
    private static Thread _threadConsumidor;

    private static readonly string dbPath           = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\ServerData.db"));
    private static readonly string connectionString = $"Data Source={dbPath}";
    static TcpListener _server = null;

    // Sensores com video
    private static readonly ConcurrentDictionary<string, (string GatewayId, string Zona, string Tipos)> _sensoresStream = new();

    #endregion

    #region INICIALIZAÇÃO

    public static void Main()
    {
        Console.CancelKeyPress += new ConsoleCancelEventHandler(TratarEncerramento);
        InicializarBaseDeDados();

        // Ler fila
        _threadConsumidor = new Thread(ConsumidorBaseDeDados)
        {
            IsBackground = true,
            Name = "DB-Writer"
        };
        _threadConsumidor.Start();

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

    // Lê da fila e insere
    static void ConsumidorBaseDeDados()
    {
        foreach (var r in _filaEscrita.GetConsumingEnumerable())
        {
            InserirNaBaseDeDados(r.GatewayId, r.SensorId, r.Zona, r.TipoDado, r.Valor, r.Timestamp, r.IsAlarm);
        }
    }

    #endregion

    #region HANDLER DE GATEWAYS

    static void HandleGateway(TcpClient gatewayClient)
    {
        string endpoint = gatewayClient.Client.RemoteEndPoint.ToString();
        try
        {
            using NetworkStream stream = gatewayClient.GetStream();
            using StreamReader reader  = new StreamReader(stream, Encoding.UTF8);
            using StreamWriter writer  = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            string linha;
            while ((linha = reader.ReadLine()) != null)
            {
                string[] partes = linha.Split('|');
                for (int i = 0; i < partes.Length; i++) { partes[i] = partes[i].Trim(); }

                if (partes.Length == 7 && (partes[0] == "DATA_FORWARD" || partes[0] == "ALARM_FORWARD"))
                {
                    bool isAlarm = partes[0] == "ALARM_FORWARD";
                    var registo  = new DataRecord(partes[1], partes[2], partes[3], partes[4], partes[5], partes[6], isAlarm);

                    // tenta de 5 em 5
                    if (_filaEscrita.TryAdd(registo, TimeSpan.FromSeconds(5)))
                    {
                        // Enviar resposta
                        writer.WriteLine("ACK_FORWARDDATA|STATUS OK");
                        if (isAlarm) RegistarLog($"[{partes[3]}] ANOMALIA! Sensor: {partes[2]} | {partes[4]} = {partes[5]}", true);
                        else         RegistarLog($"[{partes[3]}] {partes[2]} -> {partes[4]} = {partes[5]}");
                    }
                    else
                    {
                        writer.WriteLine("ACK_FORWARDDATA|ERRO FILA CHEIA");
                        RegistarLog($"Fila de escrita cheia! Dado de {partes[2]} rejeitado.");
                    }
                }
                else if (partes.Length == 6 && partes[0] == "SENSOR_REG")
                {
                    // SENSOR_REG|gatewayId|sensorId|zona|tipos|true/false
                    bool videoCapable = partes[5].Equals("true", StringComparison.OrdinalIgnoreCase);
                    if (videoCapable)
                        _sensoresStream[partes[2]] = (partes[1], partes[3], partes[4]);

                    writer.WriteLine("ACK_SENSOR_REG|OK");
                    string videoLabel = videoCapable ? "VIDEO:SIM" : "VIDEO:NAO";
                    RegistarLog($"Sensor reg.: {partes[2]} | GW:{partes[1]} | ZONA:{partes[3]} | {videoLabel}");
                }
                else writer.WriteLine("ACK_FORWARDDATA|ERRO FORMATO");
            }
        }
        catch (Exception e) { RegistarLog($"ERRO REDE ({endpoint}): {e.Message}"); }
        finally { gatewayClient.Close(); }
    }

    #endregion

    #region BASE DE DADOS

    static void InicializarBaseDeDados()
    {
        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"CREATE TABLE IF NOT EXISTS Dados (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GatewayId TEXT, SensorId TEXT, Zona TEXT,
                TipoDado TEXT, Valor TEXT, Timestamp TEXT,
                IsAlarm INTEGER DEFAULT 0)";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { RegistarLog($"Erro DB: {ex.Message}"); }
    }

    static void InserirNaBaseDeDados(string gatewayId, string sensorId, string zona,
        string tipoDado, string valor, string timestamp, bool isAlarm)
    {
        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"INSERT INTO Dados (GatewayId, SensorId, Zona, TipoDado, Valor, Timestamp, IsAlarm)
                                VALUES ($gatewayId, $sensorId, $zona, $tipoDado, $valor, $timestamp, $isAlarm)";
            cmd.Parameters.AddWithValue("$gatewayId",  gatewayId);
            cmd.Parameters.AddWithValue("$sensorId",   sensorId);
            cmd.Parameters.AddWithValue("$zona",       zona);
            cmd.Parameters.AddWithValue("$tipoDado",   tipoDado);
            cmd.Parameters.AddWithValue("$valor",      valor);
            cmd.Parameters.AddWithValue("$timestamp",  timestamp);
            cmd.Parameters.AddWithValue("$isAlarm",    isAlarm ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { RegistarLog($"Erro INSERT DB: {ex.Message}"); }
    }

    #endregion

    #region ENCERRAMENTO

    static void TratarEncerramento(object sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        _isOnline = false;
        RegistarLog("A desligar... A aguardar escrita de dados pendentes na DB.");

        _server?.Stop();

        _filaEscrita.CompleteAdding();
        _threadConsumidor.Join(TimeSpan.FromSeconds(10));

        Environment.Exit(0);
    }

    #endregion
}
