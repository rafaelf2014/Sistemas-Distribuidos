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
            Int32 port = 13000;
            IPAddress localAddr = IPAddress.Parse("127.0.0.1");

            //Crio o objeto TcpListener e o inicío
            server = new TcpListener(localAddr, port);
            server.Start();

            //Buffer para leitura dos dados
            Byte[] bytes = new Byte[256];

            // O gateway entra num loop de "ouvir".
            while (true)
            {
                Console.Write("Waiting for a connection... ");

                //Aceita o cliente
                using TcpClient client = server.AcceptTcpClient();
                Console.WriteLine("Connected!");

                //Pega o stream de dados do cliente
                NetworkStream stream = client.GetStream();

                int i;

                //loop para ler os dados do cliente
                while ((i = stream.Read(bytes, 0, bytes.Length)) != 0)
                {
                    // Traduzimos os bytes para caracteres
                    string rawData = Encoding.ASCII.GetString(bytes, 0, i);
                    Console.WriteLine($"Recebido: {rawData}");

                    string command = rawData.Split('|')[0];
                    string response = "ACK_OK";

                    switch (command)
                    {
                        case "HELLO":
                            Console.WriteLine("Log: Sensor a iniciar...");
                            break;
                        case "DATA_SEND":
                            Console.WriteLine("Log: Processando dados...");
                            EnviarParaServidor(rawData); 
                            break;
                        case "HEARTBEAT":
                            Console.WriteLine("Log: Sensor Vivo.");
                            break;
                        case "BYE":
                            Console.WriteLine("Log: Sensor a desconectar");
                            break;
                        default:
                            response = "ACK_ERR";
                            break;
                    }

                    // Responder ao Sensor
                    byte[] msg = Encoding.ASCII.GetBytes(response);
                    stream.Write(msg, 0, msg.Length);
                }
            }
        }
        catch (SocketException e)
        {
            Console.WriteLine("SocketException: {0}", e);
        }
        finally
        {
            server.Stop();
        }

        Console.WriteLine("\nHit enter to continue...");
        Console.Read();
    }

    //Método para enviar os dados processados para o servidor central
    static void EnviarParaServidor(string data)
    {
        try
        {
            using TcpClient serverClient = new TcpClient("127.0.0.1", 14000);
            using NetworkStream serverStream = serverClient.GetStream();

            byte[] dataToSend = Encoding.ASCII.GetBytes(data);
            serverStream.Write(dataToSend, 0, dataToSend.Length);
            Console.WriteLine("Dados encaminhados para o Servidor.");
        }
        catch (Exception e)
        {
            Console.WriteLine($"Falha ao contactar servidor: {e.Message}");
        }
    }
    
}

