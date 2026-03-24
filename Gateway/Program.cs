using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

class MyTcpListener
{
    public static void Main()
    {
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
            string[] parts = data.Split('|');
            parts[0] = "DATA_FORWARD"; // Altera o primeiro elemento
            string modifiedData = string.Join("|", parts); // Junta tudo de novo com pipes
            using (TcpClient serverClient = new TcpClient("127.0.0.1", 14000))
            using (NetworkStream serverStream = serverClient.GetStream())
            using (StreamWriter writer = new StreamWriter(serverStream) { AutoFlush = true })
            {
                writer.WriteLine(modifiedData);
                Console.WriteLine("Dados encaminhados para o Servidor.");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"Falha ao contactar servidor (O Servidor está a correr?): {e.Message}");
        }
    }
}