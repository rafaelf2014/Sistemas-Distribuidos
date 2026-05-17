using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using RabbitMQ.Client;

partial class MyTcpListener
{
    #region LISTENER DE COMANDOS DO SERVIDOR (PORTA 14001)

    static void IniciarListenerComandos()
    {
        var listener = new TcpListener(IPAddress.Any, 14001);
        listener.Start();
        RegistarLogEsquerda("Listener de comandos do servidor na porta 14001.");

        while (_isOnline)
        {
            try
            {
                TcpClient client = listener.AcceptTcpClient();
                new Thread(() => HandleComandoServidor(client)) { IsBackground = true }.Start();
            }
            catch { break; }
        }
    }

    static void HandleComandoServidor(TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true };

            string linha = reader.ReadLine();
            if (linha == null) return;

            string[] p = linha.Split('|');

            if (p[0] == "REQUEST_STREAM" && p.Length == 4 && int.TryParse(p[3], out int port))
            {
                EnviarComandoParaSensor(p[1], $"STREAM_TO|{p[2]}:{port}");
                writer.WriteLine("ACK_REQUEST_STREAM|OK");
                RegistarLogEsquerda($"[VIDEO] Stream pedido: {p[1]} → {p[2]}:{port}");
            }
            else if (p[0] == "STOP_STREAM" && p.Length == 2)
            {
                EnviarComandoParaSensor(p[1], "STOP_STREAM");
                writer.WriteLine("ACK_STOP_STREAM|OK");
                RegistarLogEsquerda($"[VIDEO] Stop stream: {p[1]}");
            }
            else writer.WriteLine("ACK_CMD|ERRO FORMATO");
        }
        catch (Exception ex) { RegistarLogEsquerda($"Erro cmd servidor: {ex.Message}"); }
        finally { client.Close(); }
    }

    static void EnviarComandoParaSensor(string sensorId, string comando)
    {
        if (_amqpChannel == null) return;
        try
        {
            var body = Encoding.UTF8.GetBytes(comando);
            // Publica na queue directa do sensor (default exchange, routing key = nome da queue)
            _amqpChannel.BasicPublishAsync("", $"commands.{sensorId}", body).GetAwaiter().GetResult();
            RegistarLogEsquerda($"[CMD] → {sensorId}: {comando}");
        }
        catch (Exception ex) { RegistarLogEsquerda($"[CMD] Falha a enviar comando a {sensorId}: {ex.Message}"); }
    }

    #endregion
}
