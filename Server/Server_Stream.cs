using System;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Threading;
using OpenCvSharp;

partial class ServerCentral
{
    #region CAMPOS STREAM

    // IP e porta de comando de cada gateway, aprendidos no registo dos sensores.
    readonly ConcurrentDictionary<string, string> _gatewayIps   = new();
    readonly ConcurrentDictionary<string, int>    _gatewayPorts = new();

    // Estado do stream atual (o servidor mostra um stream de cada vez).
    volatile string _streamingSensorId = null;
    volatile bool _streamingAtivo    = false;
    readonly int  _udpStreamPort     = 15000;

    #endregion

    #region GESTAO DE STREAM

    // Pede ao gateway dono do sensor que inicie o stream (via TCP de comando) e arranca
    // a thread que recebe os fotogramas por UDP.
    async Task IniciarStream(string sensorId)
    {
        if (_streamingAtivo) { RegistarLog("Já existe um stream ativo."); return; }
        if (!_sensoresStream.TryGetValue(sensorId, out var info)) return;
        if (!_gatewayIps.TryGetValue(info.GatewayId, out string gwIp))
        {
            RegistarLog($"IP do gateway {info.GatewayId} desconhecido."); return;
        }

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(gwIp, _gatewayPorts.GetValueOrDefault(info.GatewayId, 14001));
            using var s   = tcp.GetStream();
            using var r   = new StreamReader(s);
            using var w   = new StreamWriter(s) { AutoFlush = true };

            await w.WriteLineAsync($"REQUEST_STREAM|{sensorId}|{ObterIpLocal()}|{_udpStreamPort}");
            string ack = await r.ReadLineAsync();

            if (ack?.Contains("OK") == true)
            {
                _streamingSensorId = sensorId;
                _streamingAtivo    = true;
                new Thread(() => ReceberEMostrarStream(_udpStreamPort))
                    { IsBackground = true, Name = "UDP-Stream" }.Start();
                RegistarLog($"[VIDEO] Stream iniciado: {sensorId}");
            }
            else RegistarLog($"Gateway rejeitou stream: {ack}");
        }
        catch (Exception ex) { RegistarLog($"Erro ao iniciar stream: {ex.Message}"); }
    }

    // Pede ao gateway que pare o stream e limpa o estado local.
    void PararStream()
    {
        if (!_streamingAtivo || _streamingSensorId == null) return;
        string sensorId = _streamingSensorId;

        if (_sensoresStream.TryGetValue(sensorId, out var info) &&
            _gatewayIps.TryGetValue(info.GatewayId, out string gwIp))
        {
            try
            {
                using var tcp = new TcpClient(gwIp, _gatewayPorts.GetValueOrDefault(info.GatewayId, 14001));
                using var s   = tcp.GetStream();
                using var r   = new StreamReader(s);
                using var w   = new StreamWriter(s) { AutoFlush = true };
                w.WriteLine($"STOP_STREAM|{sensorId}");
                r.ReadLine();
            }
            catch { }
        }

        _streamingAtivo    = false;
        _streamingSensorId = null;
        RegistarLog($"[VIDEO] Stream de {sensorId} terminado.");
    }

    // Recebe os fotogramas JPEG por UDP, descodifica-os e mostra-os numa janela OpenCV.
    void ReceberEMostrarStream(int udpPort)
    {
        UdpClient udp = null;
        try
        {
            udp = new UdpClient(udpPort);
            udp.Client.ReceiveTimeout = 2000;
            var remoteEp = new IPEndPoint(IPAddress.Any, 0);

            while (_streamingAtivo)
            {
                try
                {
                    byte[] dados = udp.Receive(ref remoteEp);
                    if (dados.Length < 100 || dados.Length > 65000) continue;
                    using Mat frame = Cv2.ImDecode(dados, ImreadModes.Color);
                    if (!frame.Empty())
                    {
                        Cv2.ImShow($"ONE HEALTH — {_streamingSensorId}", frame);
                        if (Cv2.WaitKey(1) == 27) { PararStream(); break; }
                    }
                }
                catch (SocketException) { }
            }
        }
        catch (Exception ex) { RegistarLog($"Erro UDP stream: {ex.Message}"); }
        finally
        {
            udp?.Close();
            try { Cv2.DestroyAllWindows(); } catch { }
        }
    }

    // Descobre o IP local a anunciar ao gateway (variavel de ambiente ou deteta pela rede).
    string ObterIpLocal()
    {
        string? configured = Environment.GetEnvironmentVariable("SERVER_IP");
        if (!string.IsNullOrEmpty(configured)) return configured;

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
