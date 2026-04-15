using System;
using System.Collections.Generic;

// ==========================================
// TUI / DASHBOARD — Servidor Central
// ==========================================
partial class ServerCentral
{
    #region CAMPOS TUI

    private static readonly object _consoleLock    = new object();
    private static List<string>    _ultimosLogs    = new List<string>();
    private static List<string>    _ultimosAlarmes = new List<string>();
    private static bool            _isOnline       = true;

    #endregion

    #region REGISTO DE LOGS

    static void RegistarLog(string mensagem, bool isAlarm = false)
    {
        lock (_consoleLock)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string linha = $"[{timestamp}] {mensagem}";

            if (isAlarm)
            {
                _ultimosAlarmes.Insert(0, linha);
                if (_ultimosAlarmes.Count > 10) _ultimosAlarmes.RemoveAt(10);
            }
            else
            {
                _ultimosLogs.Insert(0, linha);
                if (_ultimosLogs.Count > 10) _ultimosLogs.RemoveAt(10);
            }
            DesenharDashboard();
        }
    }

    #endregion

    #region DASHBOARD

    static void DesenharDashboard()
    {
        try { Console.SetCursorPosition(0, 0); } catch { Console.Clear(); }
        Console.CursorVisible = false;
        string sep = new string('=', 110);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(sep);
        Console.WriteLine("                                          [ ONE HEALTH - SERVIDOR CENTRAL ]                                      ");
        Console.WriteLine(sep);
        Console.ResetColor();

        Console.Write("  ESTADO: ");
        if (_isOnline) { Console.ForegroundColor = ConsoleColor.Green; Console.Write("ONLINE "); }
        else           { Console.ForegroundColor = ConsoleColor.Red;   Console.Write("OFFLINE"); }
        Console.ResetColor();
        Console.WriteLine($"   |   FILA PENDENTE DB: {_filaEscrita.Count} registos".PadRight(94));

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(sep);
        Console.ResetColor();

        // Fixed 10-slot alarm section
        Console.WriteLine(new string(' ', 110));
        Console.WriteLine("[ ÚLTIMOS 10 ALARMES URGENTES (EDGE ANALYTICS) ]".PadRight(110));
        for (int i = 0; i < 10; i++)
        {
            if (i < _ultimosAlarmes.Count)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("   !!! ");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(_ultimosAlarmes[i].PadRight(103));
                Console.ResetColor();
            }
            else Console.WriteLine(new string(' ', 110));
        }

        // Fixed 5-slot streaming sensors section
        Console.WriteLine(new string(' ', 110));
        Console.WriteLine("[ SENSORES COM CAPACIDADE DE STREAMING ]".PadRight(110));
        var streamList = _sensoresStream.ToList();
        for (int i = 0; i < 5; i++)
        {
            if (i < streamList.Count)
            {
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.Write("   [VIDEO] ");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine($"{streamList[i].Key} | GW: {streamList[i].Value.GatewayId} | ZONA: {streamList[i].Value.Zona} | TIPOS: {streamList[i].Value.Tipos}".PadRight(99));
                Console.ResetColor();
            }
            else Console.WriteLine(new string(' ', 110));
        }

        // Fixed 10-slot activity log section
        Console.WriteLine(new string(' ', 110));
        Console.WriteLine("[ ÚLTIMAS 10 ATIVIDADES (TRÁFEGO NORMAL) ]".PadRight(110));
        for (int i = 0; i < 10; i++)
        {
            if (i < _ultimosLogs.Count)
            {
                if (_ultimosLogs[i].Contains("Erro") || _ultimosLogs[i].Contains("ERRO"))
                    Console.ForegroundColor = ConsoleColor.Red;
                else
                    Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine($"   > {_ultimosLogs[i]}".PadRight(110));
                Console.ResetColor();
            }
            else Console.WriteLine(new string(' ', 110));
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(new string(' ', 110));
        Console.WriteLine(sep);
        Console.ResetColor();
        Console.WriteLine(" Pressione Ctrl+C para encerrar o servidor de forma limpa.".PadRight(110));
    }

    #endregion
}
