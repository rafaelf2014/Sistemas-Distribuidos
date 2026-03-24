using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

class MyTcpListener
{
    private static TcpClient _serverClient;
    private static StreamWriter _serverWriter;
    private static string _gatewayId = "Gateway_001";

    public static void Connect()
    {
        try
        {
            _serverClient = new TcpClient("127.0.0.1", 14000);
            NetworkStream stream = _serverClient.GetStream();
            _serverWriter = new StreamWriter(stream) { AutoFlush = true };
            Console.WriteLine(">>> Ligado ao Servidor Central com sucesso!");
        }
        catch (Exception e)
        {
            Console.WriteLine($">>> Erro ao ligar ao Servidor Central: {e.Message}");
        }
    }

    public static void Main()
    {
        Connect(); //Estabelece a conexão persistente com o servidor central
        TcpListener server = null;

        try
        {
            Int32 port = 5000;
            IPAddress localAddr = IPAddress.Parse("127.0.0.1");

            server = new TcpListener(localAddr, port);
            server.Start();

            while (true)
            {
                Console.WriteLine("\nWaiting for a connection from a Sensor...");


                using (TcpClient client = server.AcceptTcpClient())
                {
                    Console.WriteLine("Sensor Connected!");


                    using (NetworkStream stream = client.GetStream())
                    using (StreamReader reader = new StreamReader(stream))
                    using (StreamWriter writer = new StreamWriter(stream) { AutoFlush = true })
                    {
                        string rawData;


                        while ((rawData = reader.ReadLine()) != null)
                        {
                            Console.WriteLine($"Recebido: {rawData}");

                            string[] parts = rawData.Split('|');
                            string command = parts[0].ToUpper();
                            string response = "ACK_OK";

                            switch (command)
                            {
                                case "HELLO":
                                    Console.WriteLine("Log: Sensor a iniciar...");
                                    response = "ACK_HELLO|OK";
                                    break;
                                case "DATA_SEND":
                                    Console.WriteLine("Log: Processando dados...");
                                    EnviarParaServidor(rawData);
                                    response = "ACK_DATA|OK";
                                    break;
                                case "HEARTBEAT":
                                    Console.WriteLine("Log: Sensor Vivo.");
                                    response = "ACK_HEARTBEAT|OK";
                                    break;
                                case "BYE":
                                    Console.WriteLine("Log: Sensor a desconectar");
                                    response = "ACK_BYE|OK";
                                    break;
                                default:
                                    response = "ACK_ERR|Unknown Command";
                                    break;
                            }

                            // Responder ao Sensor
                            writer.WriteLine(response);

                            if (command == "BYE")
                            {
                                break;
                            }
                        }
                    }
                    Console.WriteLine("Sensor connection closed. Ready for next sensor.");
                }
            }
        }
        catch (SocketException e)
        {
            Console.WriteLine("SocketException: {0}", e);
        }
        finally
        {
            server?.Stop();
        }
    }

    //Método para enviar os dados processados para o servidor central
    static void EnviarParaServidor(string data)
    {
        try
        {
            if (_serverWriter != null)
            {
                string[] sensorParts = data.Split('|');
                if (sensorParts.Length < 5)
                {
                    Console.WriteLine("Erro: Mensagem do sensor incompleta para reencaminhamento.");
                    return;
                }
                string gatewayId = "Gateway_001"; // ID do Gateway para identificação
                string zona = "Vila do conde"; // Zona fixa para este exemplo
                string sensorId = sensorParts[1];
                string tipoDado = sensorParts[2];
                string valor = sensorParts[3];
                string timestamp = sensorParts[4];

                string modifiedData = $"DATA_FORWARD|{gatewayId}|{sensorId}|{zona}|{tipoDado}|{valor}|{timestamp}";

                _serverWriter.WriteLine(modifiedData);
                Console.WriteLine("Dados encaminhados via conexão persistente.");
            }
            else
            {
                Console.WriteLine("Erro: Conexão com servidor central perdida. A tentar reconectar...");
                Connect(); //Tenta conectar de volta com o servidor
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"Falha ao contactar servidor (O Servidor está a correr?): {e.Message}");
        }
    }
}