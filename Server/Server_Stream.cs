using System;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using OpenCvSharp;

// ==========================================
// STREAMING — Servidor Central
// Gere a recepção UDP e a janela de visualização.
// ==========================================
partial class ServerCentral
{
    #region CAMPOS STREAM

    // Mapa gatewayId → IP (preenchido quando o gateway envia qualquer mensagem)
    static readonly ConcurrentDictionary<string, string> _gatewayIps = new();

    static string          _streamingSensorId = null;
    static volatile bool   _streamingAtivo    = false;
    static readonly int    _udpStreamPort     = 15000;

    #endregion

    #region GESTÃO DE STREAM

    static void IniciarTecladoThread()
    {
        new Thread(LerTeclado) { IsBackground = true, Name = "Keyboard" }.Start();
    }

    // Lê keypresses [1-5] para iniciar/parar streams dos sensores listados
    static void LerTeclado()
    {
        while (_isOnline)
        {
            if (!Console.KeyAvailable) { Thread.Sleep(100); continue; }
            var key = Console.ReadKey(intercept: true);
            var lista = _sensoresStream.Keys.ToList();
            int idx = key.KeyChar - '1';
            if (idx >= 0 && idx < lista.Count)
            {
                string sensorId = lista[idx];
                if (_streamingAtivo && _streamingSensorId == sensorId)
                    PararStream();
                else
                    IniciarStream(sensorId);
            }
        }
    }

    static void IniciarStream(string sensorId)
    {
        if (_streamingAtivo) { RegistarLog("Já existe um stream ativo. Pare-o primeiro com a mesma tecla."); return; }
        if (!_sensoresStream.TryGetValue(sensorId, out var info)) return;
        if (!_gatewayIps.TryGetValue(info.GatewayId, out string gwIp))
        {
            RegistarLog($"IP do gateway {info.GatewayId} ainda desconhecido."); return;
        }

        try
        {
            using var tcp = new TcpClient(gwIp, 14001);
            using var s   = tcp.GetStream();
            using var r   = new StreamReader(s);
            using var w   = new StreamWriter(s) { AutoFlush = true };

            string localIp = ObterIpLocal();
            w.WriteLine($"REQUEST_STREAM|{sensorId}|{localIp}|{_udpStreamPort}");
            string ack = r.ReadLine();

            if (ack?.Contains("OK") == true)
            {
                _streamingSensorId = sensorId;
                _streamingAtivo    = true;
                new Thread(() => ReceberEMostrarStream(_udpStreamPort))
                    { IsBackground = true, Name = "UDP-Stream" }.Start();
                RegistarLog($"[VIDEO] Stream iniciado: {sensorId} → UDP:{_udpStreamPort}");
            }
            else RegistarLog($"Gateway rejeitou pedido de stream: {ack}");
        }
        catch (Exception ex) { RegistarLog($"Erro ao iniciar stream: {ex.Message}"); }
    }

    static void PararStream()
    {
        if (!_streamingAtivo || _streamingSensorId == null) return;
        string sensorId = _streamingSensorId;

        if (_sensoresStream.TryGetValue(sensorId, out var info) &&
            _gatewayIps.TryGetValue(info.GatewayId, out string gwIp))
        {
            try
            {
                using var tcp = new TcpClient(gwIp, 14001);
                using var s   = tcp.GetStream();
                using var r   = new StreamReader(s);
                using var w   = new StreamWriter(s) { AutoFlush = true };
                w.WriteLine($"STOP_STREAM|{sensorId}");
                r.ReadLine();
            }
            catch { /* falha silenciosa — o sensor vai parar por timeout */ }
        }

        _streamingAtivo    = false;
        _streamingSensorId = null;
        RegistarLog($"[VIDEO] Stream de {sensorId} terminado.");
    }

    // Recebe frames UDP e mostra numa janela OpenCV (corre na sua própria thread)
    static void ReceberEMostrarStream(int udpPort)
    {
        UdpClient udp = null;
        try
        {
            udp = new UdpClient(udpPort);
            udp.Client.ReceiveTimeout = 2000; // permite verificar _streamingAtivo regularmente
            var remoteEp = new IPEndPoint(IPAddress.Any, 0);

            while (_streamingAtivo)
            {
                try
                {
                    byte[] dados = udp.Receive(ref remoteEp);
                    using Mat frame = Cv2.ImDecode(dados, ImreadModes.Color);
                    if (!frame.Empty())
                    {
                        Cv2.ImShow($"ONE HEALTH Stream — {_streamingSensorId}", frame);
                        if (Cv2.WaitKey(1) == 27) // ESC fecha a janela manualmente
                        {
                            PararStream();
                            break;
                        }
                    }
                }
                catch (SocketException) { /* timeout — volta a verificar _streamingAtivo */ }
            }
        }
        catch (Exception ex) { RegistarLog($"Erro UDP stream: {ex.Message}"); }
        finally
        {
            udp?.Close();
            try { Cv2.DestroyAllWindows(); } catch { }
        }
    }

    // Obtém o IP local usado para chegar à rede externa (funciona com múltiplas interfaces)
    static string ObterIpLocal()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
        }
        catch { return "127.0.0.1"; }
    }

    #endregion
}
