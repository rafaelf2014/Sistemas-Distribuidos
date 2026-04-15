using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Threading;

// ==========================================
// STREAMING — Gateway Edge
// Recebe comandos REQUEST_STREAM / STOP_STREAM do servidor (porta 14001)
// e entrega-os ao sensor via piggyback no próximo ACK.
// ==========================================
partial class MyTcpListener
{
    #region CAMPOS STREAM

    // Preenchido pelo handler do servidor; consumido pelo próximo ACK ao sensor
    static readonly ConcurrentDictionary<string, (string Ip, int Port)> _pendingStream = new();
    static readonly ConcurrentDictionary<string, bool>                  _pendingStop   = new();

    #endregion

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
                _pendingStream[p[1]] = (p[2], port);
                writer.WriteLine("ACK_REQUEST_STREAM|OK");
                RegistarLogEsquerda($"[VIDEO] Stream pedido: {p[1]} → {p[2]}:{port}");
            }
            else if (p[0] == "STOP_STREAM" && p.Length == 2)
            {
                _pendingStop[p[1]] = true;
                writer.WriteLine("ACK_STOP_STREAM|OK");
                RegistarLogEsquerda($"[VIDEO] Stop stream: {p[1]}");
            }
            else writer.WriteLine("ACK_CMD|ERRO FORMATO");
        }
        catch (Exception ex) { RegistarLogEsquerda($"Erro cmd servidor: {ex.Message}"); }
        finally { client.Close(); }
    }

    // Retorna sufixo a adicionar ao próximo ACK deste sensor, ou ""
    static string ComandoPendenteParaSensor(string sensorId)
    {
        if (_pendingStream.TryRemove(sensorId, out var req))
            return $"|STREAM_TO|{req.Ip}:{req.Port}";
        if (_pendingStop.TryRemove(sensorId, out _))
            return "|STOP_STREAM";
        return "";
    }

    #endregion
}
