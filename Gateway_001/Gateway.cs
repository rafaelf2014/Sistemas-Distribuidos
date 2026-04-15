using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using System.Globalization;
using System.Threading;
using Timer = System.Timers.Timer;

class MyTcpListener
{
    private static string _gatewayId = "Gateway_001";
    static readonly string pastaProjeto = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\"));
    static readonly string caminhoFicheiro = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\sensores.csv"));
    static readonly object fileLock = new object();
    static readonly object _bufferFileLock = new object();
    static List<Timer> _timersAgregacao = new List<Timer>();
    static Timer _timerWatchdog;
    static TcpListener server = null;

    // NOVO: Dicionário para guardar as unidades para usar na UI
    static Dictionary<string, string> _unidadesMedida = new Dictionary<string, string>();

    private static readonly object _consoleLock = new object();
    private static List<string> _alarmesEsquerda = new List<string>();
    private static List<string> _logsEsquerda = new List<string>();
    private static List<string> _logsDireita = new List<string>();
    private static bool _isOnline = true;

    public static void Main()
    {
        Console.CancelKeyPress += new ConsoleCancelEventHandler(TratarEncerramento);
        InicializarFicheiroConfiguracao();
        InicializarTimersGateway();

        _timerWatchdog = new Timer(10000);
        _timerWatchdog.Elapsed += VerificarSensoresPerdidos;
        _timerWatchdog.AutoReset = true;
        _timerWatchdog.Start();

        try
        {
            Int32 port = 5000;
            server = new TcpListener(IPAddress.Any, port);
            server.Start();

            RegistarLogEsquerda($"À escuta de sensores (Qualquer IP) na porta {port}...");

            while (true)
            {
                TcpClient client = server.AcceptTcpClient();
                Thread threadSensor = new Thread(() => HandleSensor(client));
                threadSensor.Start();
            }
        }
        catch (SocketException) { RegistarLogEsquerda("Sistema de escuta interrompido."); }
        finally { server?.Stop(); }
    }

    // ==========================================
    // CONFIGURAÇÃO DINÂMICA (COM UNIDADES)
    // ==========================================
    static void InicializarTimersGateway()
    {
        string caminhoConfig = Path.Combine(pastaProjeto, "config_gateway.csv");

        // CORREÇÃO: Formato -> DATATYPE ; UNIDADE ; INTERVALO_MS
        if (!File.Exists(caminhoConfig))
        {
            File.WriteAllText(caminhoConfig, "TEMP;ºC;30000\nHUM;%;60000\nCO2;ppm;90000\n");
        }

        string[] linhas = File.ReadAllLines(caminhoConfig);

        foreach (string linha in linhas)
        {
            if (string.IsNullOrWhiteSpace(linha)) continue;
            string[] col = linha.Split(';');
            if (col.Length >= 3) // Precisa de Tipo, Unidade e Intervalo
            {
                string tipoDado = col[0].Trim().ToUpper();
                string unidade = col[1].Trim();
                if (int.TryParse(col[2].Trim(), out int intervalo))
                {
                    // Guarda a unidade no dicionário!
                    _unidadesMedida[tipoDado] = unidade;

                    Timer t = new Timer(intervalo);
                    t.Elapsed += (s, e) => ProcessarAgregadosFiltrados(tipoDado);
                    t.AutoReset = true;
                    t.Start();
                    _timersAgregacao.Add(t);
                    RegistarLogEsquerda($"SYS: Agregador [{tipoDado}] ({unidade}) a cada {intervalo / 1000}s.");
                }
            }
        }
    }

    static void ProcessarAgregadosFiltrados(string tipoDadoFiltro)
    {
        //Recolhe todos os ficheiros com o tipo de dado específico
        string[] ficheirosBrutos = Directory.GetFiles(pastaProjeto, $"pendente_*_{tipoDadoFiltro}.csv");

        //Ordena por ordem cronológica
        var ficheirosOrdenados = ficheirosBrutos.OrderBy(f => f).ToList();

        foreach (string ficheiro in ficheirosOrdenados)
        {
            try
            {
                string[] partes = Path.GetFileNameWithoutExtension(ficheiro).Split('_');
                if (partes.Length != 4) continue;
                string ticksStr = partes[1]; string sensorId = partes[2]; string tipoDado = partes[3];

                string[] linhas = null; //Criamos uma lista vazia onde vamos guardar as linhas do ficheiro
                //Criamos um lock de segurança para prevenir que uma thread apague o ficheiro enquanto outra thread lê o conteúdo
                lock (_bufferFileLock)
                {
                    if (!File.Exists(ficheiro)) continue;
                    linhas = File.ReadAllLines(ficheiro);
                    File.Delete(ficheiro);
                }
                //Se o ficheiro estiver vazio não vale a pena guardar.
                if (linhas.Length == 0)
                {
                    lock (_bufferFileLock) { File.Delete(ficheiro); }
                    continue;
                }

                List<double> valores = new List<double>();
                foreach (string linha in linhas) { if (double.TryParse(linha, NumberStyles.Any, CultureInfo.InvariantCulture, out double val)) valores.Add(val); }

                if (valores.Count > 0)
                {
                    double media = valores.Average();
                    string timestampHistorico = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                    string payload = $"DATA_SEND|{sensorId}|{tipoDado}|{media.ToString("F2", CultureInfo.InvariantCulture)}|{timestampHistorico}";

                    if (long.TryParse(ticksStr, out long ticksOriginais)) timestampHistorico = new DateTime(ticksOriginais).ToString("yyyy-MM-ddTHH:mm:ss");

                    if (EnviarParaServidor(payload))
                    {
                        RegistarLogEsquerda($"Forward pendente de {sensorId} ({tipoDado}) OK.");
                        Task.Run(() => DrenarFilaOffline()); //Disparamos a thread de recuperação em background
                    }
                    else
                    {
                        string filaOffline = Path.Combine(pastaProjeto, "fila_offline_gateway.txt");

                        lock (_bufferFileLock)
                        {
                            File.AppendAllText(filaOffline, payload + Environment.NewLine);
                        }

                        RegistarLogEsquerda($"Servidor Offline: Agregação das {timestampHistorico} guardada na fila.", true);
                    }
                }
            }
            catch (Exception ex) { RegistarLogEsquerda($"Erro pendente: {ex.Message}"); }
        }
    }

    static void HandleSensor(TcpClient client)
    {
        try
        {
            using NetworkStream stream = client.GetStream();
            using StreamReader reader = new StreamReader(stream);
            using StreamWriter writer = new StreamWriter(stream) { AutoFlush = true };
            string rawData;
            while ((rawData = reader.ReadLine()) != null)
            {
                string[] parts = rawData.Split('|');
                string command = parts[0].ToUpper();
                string resposta = "ACK_OK";

                switch (command)
                {
                    case "HELLO":
                        if (parts.Length >= 4) RegistarOuAtualizarSensor(parts[1], parts[2], parts[3]);
                        resposta = "ACK_HELLO|OK";
                        break;

                    case "DATA_SEND":
                        if (double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out double valor))
                        {
                            if (ValidarSensor(parts[1], parts[2]))
                            {
                                string sensorId = parts[1];
                                string tipoDado = parts[2];
                                string ficheiroAlvo = "";

                                // Trancamos o thread ao ficheiro para que não aconteça de dois threads tentarem manipular os dados do mesmo ficheiro ao mesmo tempo (Daria um erro de IO)
                                lock (_bufferFileLock)
                                {
                                    // Guardamos todos os ficheiros existentes com o sensorId e o tipo de sensor atual
                                    string[] ficheirosExistentes = Directory.GetFiles(pastaProjeto, $"pendente_*_{sensorId}_{tipoDado}.csv");

                                    // Se existir um ficheiro desses, então vamos escrever nele
                                    if (ficheirosExistentes.Length > 0)
                                    {
                                        ficheiroAlvo = ficheirosExistentes[0];
                                    }
                                    else    // Caso contrário, criamos um ficheiro novo com os ticks do momento da criação
                                    {
                                        long currentTick = DateTime.UtcNow.Ticks;
                                        ficheiroAlvo = Path.Combine(pastaProjeto, $"pendente_{currentTick}_{sensorId}_{tipoDado}.csv");
                                    }
                                    File.AppendAllText(ficheiroAlvo, valor.ToString(CultureInfo.InvariantCulture) + Environment.NewLine);
                                }

                                // Vai buscar a unidade, se não existir mete string vazia
                                string un = _unidadesMedida.ContainsKey(parts[2]) ? _unidadesMedida[parts[2]] : "";
                                RegistarLogEsquerda($"{parts[1]}: {parts[2]} = {valor}{un}");

                                resposta = "ACK_DATA|OK";
                            }
                            else resposta = "ACK_DATA|ERRO_VALIDACAO";
                        }
                        break;
                    case "ALARM_SEND":
                        if (ValidarSensor(parts[1], parts[2]))
                        {
                            lock (_bufferFileLock) { File.AppendAllText(Path.Combine(pastaProjeto, $"buffer_{parts[1]}_{parts[2]}.csv"), parts[3] + Environment.NewLine); }

                            string un = _unidadesMedida.ContainsKey(parts[2]) ? _unidadesMedida[parts[2]] : "";
                            RegistarLogEsquerda($"ANOMALIA: {parts[1]} registou {parts[3]}{un}!", true);

                            EnviarParaServidor(rawData);
                            resposta = "ACK_ALARM|OK";
                        }
                        else resposta = "ACK_ALARM|ERRO_VALIDACAO";
                        break;

                    case "HEARTBEAT":
                        atualizarLastSync(parts[1]);
                        resposta = "ACK_HEARTBEAT|OK";
                        break;

                    case "VIDEO_REQ":
                        RegistarLogEsquerda($"EDGE AI: Vídeo pedido por {parts[1]}...", true);
                        resposta = "ACK_VIDEO|OK";
                        break;

                    case "BYE":
                        if (parts.Length >= 2) AtualizarEstadoSensor(parts[1], "desativado");
                        resposta = "ACK_BYE|OK";
                        break;

                    default:
                        resposta = "ACK_ERR|Comando Desconhecido";
                        break;
                }
                writer.WriteLine(resposta);
                if (command == "BYE") break;
            }
        }
        catch (Exception) { }
        finally { client.Close(); }
    }

    static void InicializarFicheiroConfiguracao() { lock (fileLock) { if (!File.Exists(caminhoFicheiro)) File.WriteAllText(caminhoFicheiro, ""); } }

    static void RegistarOuAtualizarSensor(string id, string zona, string tipos)
    {
        lock (fileLock)
        {
            if (!File.Exists(caminhoFicheiro)) return;
            var linhas = File.ReadAllLines(caminhoFicheiro).ToList();
            bool encontrado = false;
            string timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

            for (int i = 0; i < linhas.Count; i++)
            {
                var col = linhas[i].Split(';');
                if (col.Length >= 5 && col[0].Trim() == id.Trim())
                {
                    col[1] = "ativo"; col[2] = zona; col[3] = tipos; col[4] = timestamp;
                    linhas[i] = string.Join(";", col);
                    encontrado = true;
                    RegistarLogEsquerda($"Config: Sensor {id} atualizado.");
                    break;
                }
            }
            if (!encontrado)
            {
                linhas.Add($"{id};ativo;{zona};{tipos};{timestamp}");
                RegistarLogEsquerda($"Config: Novo sensor {id} registado.");
            }
            File.WriteAllLines(caminhoFicheiro, linhas);
        }
    }

    static bool ValidarSensor(string id, string tipoDados) { lock (fileLock) { if (!File.Exists(caminhoFicheiro)) return false; return File.ReadAllLines(caminhoFicheiro).Select(l => l.Split(';')).Any(c => c.Length >= 5 && c[0] == id && c[1] == "ativo" && c[3].Contains(tipoDados)); } }

    static void atualizarLastSync(string id)
    {
        lock (fileLock)
        {
            if (!File.Exists(caminhoFicheiro)) return;
            var linhas = File.ReadAllLines(caminhoFicheiro);
            for (int i = 0; i < linhas.Length; i++)
            {
                var col = linhas[i].Split(';');
                if (col.Length >= 5 && col[0].Trim() == id.Trim())
                {
                    col[4] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                    linhas[i] = string.Join(";", col);
                    File.WriteAllLines(caminhoFicheiro, linhas);
                    break;
                }
            }
        }
    }

    static void AtualizarEstadoSensor(string id, string estado)
    {
        lock (fileLock)
        {
            if (!File.Exists(caminhoFicheiro)) return;
            var linhas = File.ReadAllLines(caminhoFicheiro);
            for (int i = 0; i < linhas.Length; i++)
            {
                var col = linhas[i].Split(';');
                if (col.Length >= 5 && col[0].Trim() == id.Trim())
                {
                    col[1] = estado;
                    linhas[i] = string.Join(";", col);
                    File.WriteAllLines(caminhoFicheiro, linhas);
                    RegistarLogEsquerda($"Sensor {id} {estado}.");
                    break;
                }
            }
        }
    }

    static string ObterZonaDoSensor(string id)
    {
        lock (fileLock)
        {
            if (!File.Exists(caminhoFicheiro)) return "ZONA DESCONHECIDA";
            foreach (var linha in File.ReadAllLines(caminhoFicheiro))
            {
                var col = linha.Split(';');
                if (col.Length >= 5 && col[0].Trim() == id.Trim()) return col[2];

            }
        }
        return "ZONA DESCONHECIDA";
    }

    static void VerificarSensoresPerdidos(object sender, ElapsedEventArgs e)
    {
        lock (fileLock)
        {
            if (!File.Exists(caminhoFicheiro)) return;
            var linhas = File.ReadAllLines(caminhoFicheiro).ToList();
            bool alterado = false;
            for (int i = 0; i < linhas.Count; i++)
            {
                var col = linhas[i].Split(';');
                if (col.Length >= 5 && col[1] == "ativo" && DateTime.TryParse(col[4], out DateTime lastSync) && (DateTime.Now - lastSync).TotalSeconds > 30)
                {
                    col[1] = "manutencao"; linhas[i] = string.Join(";", col); alterado = true;
                    RegistarLogEsquerda($"Watchdog: Sensor {col[0]} perdido (Timeout).", true);
                }
            }
            if (alterado) File.WriteAllLines(caminhoFicheiro, linhas);
        }
    }

    static bool EnviarParaServidor(string data)
    {
        try
        {
            using (TcpClient sc = new TcpClient("127.0.0.1", 14000))
            using (NetworkStream s = sc.GetStream())
            using (StreamReader r = new StreamReader(s))
            using (StreamWriter w = new StreamWriter(s) { AutoFlush = true })
            {
                string[] p = data.Split('|');
                string comandoForward = p[0] == "ALARM_SEND" ? "ALARM_FORWARD" : "DATA_FORWARD";

                string modified = $"{comandoForward}|{_gatewayId}|{p[1]}|{ObterZonaDoSensor(p[1])}|{p[2]}|{p[3]}|{p[4]}";
                w.WriteLine(modified);

                string resposta = r.ReadLine();

                string tag = comandoForward == "ALARM_FORWARD" ? "[ALARM]" : "[DATA]";
                string un = _unidadesMedida.ContainsKey(p[2]) ? _unidadesMedida[p[2]] : "";
                string msgVisor = $"{tag} {p[1]} ({p[2]}={p[3]}{un})"; // INCLUI UNIDADE

                RegistarLogDireita($"ENVIADO: {msgVisor}", $"RESPOSTA: {resposta}");

                if (resposta != null && resposta.Contains("STATUS OK")) return true;
            }
        }
        catch (Exception ex)
        {
            RegistarLogDireita($"ENVIADO: [Tentativa Falhada]", $"ERRO: {ex.Message}");
        }
        return false;
    }

    static void DrenarFilaOffline()
    {
        string filaOffline = Path.Combine(pastaProjeto, "fila_offline_gateway.txt");
        List<string> linhasPendentes = new List<string>();

        lock (_bufferFileLock)
        {
            if (!File.Exists(filaOffline)) return;

            linhasPendentes = File.ReadAllLines(filaOffline).ToList(); //Lemos e guardamos todo o conteudo na memória ram do gateway
            if (linhasPendentes.Count == 0) return;

            // Esvaziamos o ficheiro instantaneamente para não perdermos dados que possam ter sido enviados quando o servidor perdeu a conexão.
            // Se a rede cair daqui a 5 segundos, o Gateway começa a escrever num ficheiro limpo.
            File.WriteAllText(filaOffline, string.Empty);
        }

        // Procuramos as linhas que não foram enviadas com sucesso
        RegistarLogEsquerda($"Recuperação: a enviar {linhasPendentes.Count} registos antigos...");
        List<string> linhasFalhadas = new List<string>();
        for (int i = 0; i < linhasPendentes.Count; i++)
        {
            string payload = linhasPendentes[i];
            if (string.IsNullOrWhiteSpace(payload)) continue;

            // Se o envio falhar a meio, significa que o servidor caiu.
            if (!EnviarParaServidor(payload))
            {
                // Guardamos a linha que falhou e TODAS as que faltavam enviar
                linhasFalhadas = linhasPendentes.Skip(i).ToList();
                break;
            }
        }

        // Para o caso da conexão falhar a meio da devolução
        if (linhasFalhadas.Count > 0)
        {
            lock (_bufferFileLock)
            {
                // Devolvemos apenas os que falharam ao ficheiro.
                // Usamos Append porque o Gateway pode ter escrito dados novos entretanto!
                File.AppendAllLines(filaOffline, linhasFalhadas);
            }
            RegistarLogEsquerda($"Recuperação abortada. {linhasFalhadas.Count} devolvidos à fila.", true);
        }
        else
        {
            RegistarLogEsquerda("Recuperação offline totalmente concluída.");
        }
    }

    // ==========================================
    // LÓGICA DO DASHBOARD UI
    // ==========================================
    static void RegistarLogEsquerda(string mensagem, bool isAlarm = false)
    {
        lock (_consoleLock)
        {
            string linha = $"[{DateTime.Now:HH:mm:ss}] {mensagem}";
            if (isAlarm)
            {
                _alarmesEsquerda.Insert(0, linha);
                if (_alarmesEsquerda.Count > 10) _alarmesEsquerda.RemoveAt(10);
            }
            else
            {
                _logsEsquerda.Insert(0, linha);
                if (_logsEsquerda.Count > 10) _logsEsquerda.RemoveAt(10);
            }
            DesenharDashboard();
        }
    }

    static void RegistarLogDireita(string msgEnvio, string msgResposta)
    {
        lock (_consoleLock)
        {
            string t = DateTime.Now.ToString("HH:mm:ss");
            _logsDireita.Insert(0, $"   └─> {msgResposta}");
            _logsDireita.Insert(0, $"[{t}] {msgEnvio}");
            while (_logsDireita.Count > 20) _logsDireita.RemoveAt(_logsDireita.Count - 1);
            DesenharDashboard();
        }
    }

    static void DesenharDashboard()
    {
        Console.Clear();
        string separador = new string('=', 118);
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(separador);
        Console.WriteLine("                                            [ ONE HEALTH - GATEWAY EDGE ]                                           ");
        Console.WriteLine(separador);
        Console.ResetColor();
        Console.Write($"  ESTADO: ");
        if (_isOnline) { Console.ForegroundColor = ConsoleColor.Green; Console.Write("ONLINE"); }
        else { Console.ForegroundColor = ConsoleColor.Red; Console.Write("OFFLINE"); }
        Console.ResetColor();
        Console.WriteLine($"   |   NODE ID: {_gatewayId}");
        Console.WriteLine(separador);
        List<string> leftCol = new List<string>();
        leftCol.Add("[ ALARMES & EVENTOS CRÍTICOS ]");
        if (_alarmesEsquerda.Count == 0) leftCol.Add("   Sem ocorrências.");
        else foreach (var a in _alarmesEsquerda) leftCol.Add("!!! " + a);
        while (leftCol.Count < 12) leftCol.Add("");

        leftCol.Add("[ TRÁFEGO RECEBIDO (SENSORES) ]");
        foreach (var l in _logsEsquerda) leftCol.Add("> " + l);
        while (leftCol.Count < 24) leftCol.Add("");

        List<string> rightCol = new List<string>();
        rightCol.Add("[ OUTPUT PARA O SERVIDOR CENTRAL ]");
        foreach (var r in _logsDireita) rightCol.Add(r);
        while (rightCol.Count < 24) rightCol.Add("");

        for (int i = 0; i < 24; i++)
        {
            string left = leftCol[i].Length > 56 ? leftCol[i].Substring(0, 53) + "..." : leftCol[i];
            string right = rightCol[i].Length > 58 ? rightCol[i].Substring(0, 55) + "..." : rightCol[i];

            if (left.Contains("!!!") || left.Contains("ANOMALIA") || left.Contains("Falha Servidor") || left.Contains("Watchdog"))
                Console.ForegroundColor = ConsoleColor.Red;
            else if (left.Contains("[ ALARMES") || left.Contains("[ TRÁFEGO"))
                Console.ForegroundColor = ConsoleColor.Cyan;
            else
                Console.ForegroundColor = ConsoleColor.Gray;

            Console.Write(left.PadRight(58));

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(" | ");

            if (right.Contains("[ALARM]"))
                Console.ForegroundColor = ConsoleColor.Yellow;
            else if (right.Contains("[DATA]"))
                Console.ForegroundColor = ConsoleColor.White;
            else if (right.Contains("STATUS OK"))
                Console.ForegroundColor = ConsoleColor.Green;
            else if (right.Contains("ERRO") || right.Contains("Falhada"))
                Console.ForegroundColor = ConsoleColor.Red;
            else if (right.Contains("[ OUTPUT"))
                Console.ForegroundColor = ConsoleColor.Cyan;
            else
                Console.ForegroundColor = ConsoleColor.Gray;

            Console.WriteLine(right);
            Console.ResetColor();
        }

        Console.WriteLine(separador);
        Console.WriteLine(" Pressione Ctrl+C para desligar o Gateway de forma segura.");
    }

    static void TratarEncerramento(object sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        _isOnline = false;
        RegistarLogEsquerda("A encerrar o Gateway. Dados em buffer salvaguardados.");

        foreach (var t in _timersAgregacao) t.Stop();
        _timerWatchdog?.Stop();

        server?.Stop();
        Thread.Sleep(500);
        Environment.Exit(0);
    }
}