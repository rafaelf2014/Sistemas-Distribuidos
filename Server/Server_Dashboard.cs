using System;

partial class ServerCentral
{
    // Estado de execucao do servidor; a false termina os ciclos de aceitacao.
    private bool _isOnline = true;

    // Escreve uma linha de log na consola, com prefixo de info ou alarme.
    void RegistarLog(string mensagem, bool isAlarm = false)
    {
        string prefix = isAlarm ? "[ALARME]" : "[INFO]";
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {prefix} {mensagem}");
    }
}
