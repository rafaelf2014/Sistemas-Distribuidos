using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

class MyTcpListener
{
    private static readonly object lockFiles = new object();

    public static void Main()
    {
        TcpListener server = null;
        try
        {
            Int32 port = 14000;
            IPAddress localAddr = IPAddress.Parse("127.0.0.1");

            // TcpListener server = new TcpListener(port);
            server = new TcpListener(localAddr, port);

            // Start listening for client requests.
            server.Start();

            // Enter the listening loop.
            while (true)
            {
                Console.Write("Waiting for a connection... ");

                TcpClient client = server.AcceptTcpClient();
                Console.WriteLine("Connected!");

                Thread clientThread = new Thread(() => HandleGateway(client)) ;
                clientThread.Start();
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

    static void HandleGateway(TcpClient gatewayClient) 
    {
        try
        {
            using NetworkStream stream = gatewayClient.GetStream();
            using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
            using StreamWriter writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            string linha;

            while ((linha = reader.ReadLine()) != null)
            {
                Console.WriteLine($"[RECEBIDO] {linha}");

                string[] partes = linha.Split('|');

                // Trim whitespace from each part
                for (int i = 0; i < partes.Length; i++) { partes[i] = partes[i].Trim(); }

                // Fazer dinamicamente

                // Expected format: "DATA_forward | gatewayId | sensorId | zona | tipoDado | valor | timestamp"
                if (partes.Length == 7 && partes[0] == "DATA_FORWARD")
                {
                    string gatewayId = partes[1];
                    string sensorId = partes[2];
                    string zona = partes[3];
                    string tipoDado = partes[4]; 
                    string valor = partes[5];
                    string timestamp = partes[6];

                    
                    string nameFile = $"{tipoDado}.csv";

                    // Ensure thread-safe access to the file
                    lock (lockFiles)
                    {
                        // Check if the file exists, if not create it and write the header
                        using (StreamWriter sw = new StreamWriter(nameFile, true))
                        {
                            
                            sw.WriteLine($"{timestamp};{gatewayId};{sensorId};{zona};{valor}");
                        }
                    }

                    // Sending ACK back to Gateway
                    writer.WriteLine("ACK_FORWARDDATA | STATUS OK");
                }
            }

        }
        catch (Exception e)
        {
            Console.WriteLine($"[ERRO COMUNICAÇÃO GATEWAY] {e.Message}");
        }
        finally
        {
            
            gatewayClient.Close();
            Console.WriteLine("[SERVIDOR] Conexão com Gateway terminada.");
        }
    }

}