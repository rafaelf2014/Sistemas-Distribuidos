using System;

partial class ServerCentral
{
    private bool _isOnline = true;

    void RegistarLog(string mensagem, bool isAlarm = false)
    {
        string prefix = isAlarm ? "[ALARME]" : "[INFO]";
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {prefix} {mensagem}");
    }
}
