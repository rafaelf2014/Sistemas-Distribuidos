using System;
using System.IO;
using System.Linq.Expressions;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;

class ServerCentral
{
    private static readonly object _dbLock = new object();

    private static readonly object dbPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\ServerData.db"));
    private static readonly string connectionString = $"Data Source={dbPath}";

    public static void Main()
    {
        Console.WriteLine("A iniciar o Servidor...");

        InicializarBaseDeDados();

        TcpListener server = null;
        try
        {
            Int32 port = 14000;
            IPAddress localAddr = IPAddress.Parse("127.0.0.1");
            server = new TcpListener(localAddr, port);
            server.Start();

            Console.WriteLine($"\n[SERVIDOR] À escuta de Gateways na porta {port}...");
            Console.WriteLine($"[NOTINHA!!!!!!!!!!!] O ficheiro DB está em: {dbPath}\n");

            while (true)
            {
                TcpClient client = server.AcceptTcpClient();

                Thread clientThread = new Thread(() => HandleGateway(client)) ;
                clientThread.Start();
            }
        }
        catch (SocketException e)
        {
            Console.WriteLine($"Erro: {e.Message}");
        }
        finally
        {
            server?.Stop();
        }
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
                    var createCommand = connection.CreateCommand();
                    createCommand.CommandText = @"
                        CREATE TABLE IF NOT EXISTS Dados (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            GatewayId TEXT,
                            SensorId TEXT,
                            Zona TEXT,
                            TipoDado TEXT,
                            Valor TEXT,
                            Timestamp TEXT
                        )";
                    createCommand.ExecuteNonQuery();
                }
                Console.WriteLine("Base de dados inicializada com sucesso!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao inicializar a base de dados: {ex.Message}");
            }
        }
    }

    static void HandleGateway(TcpClient gatewayClient) 
    {
        string endpoint = gatewayClient.Client.RemoteEndPoint.ToString();
        Console.WriteLine($"Conexão estabelecida com Gateway em: {endpoint}");
        
        try
        {
            using NetworkStream stream = gatewayClient.GetStream();
            using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
            using StreamWriter writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            {

                string linha;
                while ((linha = reader.ReadLine()) != null)
                {
                    string[] partes = linha.Split('|');
                    for (int i = 0; i < partes.Length; i++) { partes[i] = partes[i].Trim(); }

                    if (partes.Length == 7 && partes[0] == "DATA_FORWARD")
                    {
                        string gatewayId = partes[1];
                        string sensorId = partes[2];
                        string zona = partes[3];
                        string tipoDado = partes[4]; 
                        string valor = partes[5];
                        string timestamp = partes[6];

                        bool inserido = InserirNaBaseDeDados(gatewayId, sensorId, zona, tipoDado, valor, timestamp);

                        if (inserido)
                        {
                            writer.WriteLine($"ACK_FOWARDDATA | STATUS OK");
                        }
                        else
                        {
                            writer.WriteLine($"ACK_FOWARDDATA | ERRO DB");
                        }
                    }
                    else
                    {
                        writer.WriteLine("ACK_FORWARDDATA | ERRO FORMATO");
                    }
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"ERRO: {e.Message}");
        }
        finally
        {
            gatewayClient.Close();
            Console.WriteLine($"Conexão com Gateway terminada em: {endpoint}");
        }
    }
    
    static bool InserirNaBaseDeDados(string gatewayId, string sensorId, string zona, string tipoDado, string valor, string timestamp)
    {
        lock (_dbLock)
        {
            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    var insertCommand = connection.CreateCommand();
                    insertCommand.CommandText = @"
                        INSERT INTO Dados (GatewayId, SensorId, Zona, TipoDado, Valor, Timestamp)
                        VALUES ($gatewayId, $sensorId, $zona, $tipoDado, $valor, $timestamp)";
                    insertCommand.Parameters.AddWithValue("$gatewayId", gatewayId);
                    insertCommand.Parameters.AddWithValue("$sensorId", sensorId);
                    insertCommand.Parameters.AddWithValue("$zona", zona);
                    insertCommand.Parameters.AddWithValue("$tipoDado", tipoDado);
                    insertCommand.Parameters.AddWithValue("$valor", valor);
                    insertCommand.Parameters.AddWithValue("$timestamp", timestamp);

                    int rowsAffected = insertCommand.ExecuteNonQuery();
                    if (rowsAffected > 0)
                    {
                        Console.WriteLine($"Dados inseridos com sucesso: {gatewayId} | {sensorId} | {zona} | {tipoDado} | {valor} | {timestamp}");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao inserir na base de dados: {ex.Message}");
            }
            return false;
        }
    }
}