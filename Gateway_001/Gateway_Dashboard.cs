using System;
using System.Collections.Generic;

// ==========================================
// TUI / DASHBOARD — Gateway Edge
// ==========================================
partial class MyTcpListener
{
    #region CAMPOS TUI

    private static readonly object       _consoleLock     = new object();
    private static readonly List<string> _alarmesEsquerda = new();
    private static readonly List<string> _logsEsquerda    = new();
    private static readonly List<string> _logsDireita     = new();
    private static bool _isOnline = true;

    #endregion

    #region REGISTO DE LOGS

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

    #endregion

    #region DASHBOARD

    static void DesenharDashboard()
    {
        try { Console.SetCursorPosition(0, 0); } catch { Console.Clear(); }
        Console.CursorVisible = false;

        string sep = new string('=', 118);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(sep);
        Console.WriteLine("                                            [ ONE HEALTH - GATEWAY EDGE ]                                           ");
        Console.WriteLine(sep);
        Console.ResetColor();

        Console.Write("  ESTADO: ");
        if (_isOnline) { Console.ForegroundColor = ConsoleColor.Green; Console.Write("ONLINE "); }
        else           { Console.ForegroundColor = ConsoleColor.Red;   Console.Write("OFFLINE"); }
        Console.ResetColor();
        Console.WriteLine($"   |   NODE ID: {_gatewayId}".PadRight(90));
        Console.WriteLine(sep);

        var leftCol  = new List<string>();
        var rightCol = new List<string>();

        leftCol.Add("[ ALARMES & EVENTOS CRITICOS ]");
        if (_alarmesEsquerda.Count == 0) leftCol.Add("   Sem ocorrencias.");
        else foreach (var a in _alarmesEsquerda) leftCol.Add("!!! " + a);
        while (leftCol.Count < 12) leftCol.Add("");

        leftCol.Add("[ TRAFEGO RECEBIDO (SENSORES) ]");
        foreach (var l in _logsEsquerda) leftCol.Add("> " + l);
        while (leftCol.Count < 24) leftCol.Add("");

        rightCol.Add("[ OUTPUT PARA O SERVIDOR CENTRAL ]");
        foreach (var r in _logsDireita) rightCol.Add(r);
        while (rightCol.Count < 24) rightCol.Add("");

        for (int i = 0; i < 24; i++)
        {
            string left  = leftCol[i].Length  > 56 ? leftCol[i].Substring(0, 53)  + "..." : leftCol[i];
            string right = rightCol[i].Length > 58 ? rightCol[i].Substring(0, 55) + "..." : rightCol[i];

            if      (left.Contains("!!!") || left.Contains("Falha") || left.Contains("Watchdog")) Console.ForegroundColor = ConsoleColor.Red;
            else if (left.Contains("[ ALARMES") || left.Contains("[ TRAFEGO"))                   Console.ForegroundColor = ConsoleColor.Cyan;
            else if (left.Contains("[VIDEO]"))                                                    Console.ForegroundColor = ConsoleColor.Magenta;
            else                                                                                  Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write(left.PadRight(58));

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(" | ");

            if      (right.Contains("[ALARM]"))                          Console.ForegroundColor = ConsoleColor.Yellow;
            else if (right.Contains("[DATA]"))                           Console.ForegroundColor = ConsoleColor.White;
            else if (right.Contains("STATUS OK"))                        Console.ForegroundColor = ConsoleColor.Green;
            else if (right.Contains("ERRO") || right.Contains("Falha"))  Console.ForegroundColor = ConsoleColor.Red;
            else if (right.Contains("[ OUTPUT"))                         Console.ForegroundColor = ConsoleColor.Cyan;
            else                                                          Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(right.PadRight(57));

            Console.ResetColor();
        }

        Console.WriteLine(sep);
        Console.WriteLine(" Pressione Ctrl+C para desligar o Gateway de forma segura.".PadRight(118));
    }

    #endregion
}
