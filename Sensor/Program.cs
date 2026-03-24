using System;
using System.IO;
using System.Net.Sockets;

namespace sensor
{
    class Program
    {
        static void Main(string[] args)
        {
            string ipGateway = "127.0.0.1";
            int portGateway = 5000;

            if (args.Length > 0)
            {
                ipGateway = args[0];
            } 
            else
            {
                Console.WriteLine("IP não fornecido. Default: 127.0.0.1");
            }

            string idSensor = "S001";
            string dataTypes = "TEMP, HUM";

            try
            {
                using (TcpClient client = new TcpClient(ipGateway, portGateway))
                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream))
                using (StreamWriter writer = new StreamWriter(stream) {AutoFlush = true})
                {
                    Console.WriteLine($"[SENSOR {idSensor}] ligado com sucesso ao Gateway em {ipGateway}:{portGateway}");

                    bool running = true;

                    while (running)
                    {
                        Console.WriteLine("\n ---------- Menu Sensor ----------");
                        Console.WriteLine("1 - Estabelecer Comunicação [HELLO]");
                        Console.WriteLine("2 - Enviar Dados [DATA_SEND]");
                        Console.WriteLine("3 - Enviar HeartBeat [HEARTBEAT");
                        Console.WriteLine("4 - Desligar [BYE]");
                        Console.WriteLine("\nEscolha uma opção");

                        string option = Console.ReadLine();
                        switch(option)
                        {
                            case "1":
                                string msgHello = $"HELLO|{idSensor}|{dataTypes}]";
                                writer.WriteLine(msgHello);
                                Console.WriteLine($"[ENVIADO]: {msgHello}");

                                string ackHello = reader.ReadLine();
                                Console.WriteLine($"[RECEBIDO]: {ackHello}");
                                break;

                            case "2":
                                string timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

                                string msgData = $"DATA_SEND|{idSensor}|TEMP|{timestamp}";
                                writer.WriteLine(msgData);
                                Console.WriteLine($"[ENVIADO]: {msgData}";

                                string ackData = reader.ReadLine();
                                Console.WriteLine($"[RECEBIDO]: {ackData}");
                                break;

                            case "3":
                                Console.WriteLine("Ainda por implementar...");
                                break;

                            case "4":
                                string msgBye = $"BYE|{idSensor}";
                                writer.WriteLine(msgBye);
                                Console.WriteLine("[ENVIADO]: A terminar comunicação corretamente... [cite: 50]");
                                running = false;
                                break;

                            default:
                                Console.WriteLine("Opção Invalida");
                                break;
                        }

                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[ERRO]: {ex.Message}"); }

        }

    }   
}