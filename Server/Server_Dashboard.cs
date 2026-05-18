using System;

partial class ServerCentral
{
    private static bool _isOnline = true;

    static void RegistarLog(string mensagem, bool isAlarm = false)
    {
        string prefix = isAlarm ? "[ALARME]" : "[INFO]";
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {prefix} {mensagem}");
    }
}
