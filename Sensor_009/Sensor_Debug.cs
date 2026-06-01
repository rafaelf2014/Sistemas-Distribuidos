using System;
using System.Globalization;
using System.Threading;

namespace sensor
{
    partial class Program
    {
        #region DEBUG MENU

        internal static void IniciarMenuDebug()
        {
            var t = new Thread(LoopMenuDebug) { IsBackground = true, Name = "DebugMenu" };
            t.Start();
        }

        static void LoopMenuDebug()
        {
            Thread.Sleep(600); // let the TUI render first
            while (!_encerrando)
            {
                try
                {
                    var key = Console.ReadKey(intercept: true);
                    lock (_consoleLock)
                    {
                        if (key.Key == ConsoleKey.Enter)
                        {
                            string cmd = _debugInput.ToString().Trim();
                            _debugInput.Clear();
                            if (!string.IsNullOrEmpty(cmd))
                                ProcessarComandoDebug(cmd);
                        }
                        else if (key.Key == ConsoleKey.Backspace)
                        {
                            if (_debugInput.Length > 0)
                                _debugInput.Remove(_debugInput.Length - 1, 1);
                        }
                        else if (!char.IsControl(key.KeyChar))
                        {
                            _debugInput.Append(key.KeyChar);
                        }
                        DesenharDashboard();
                    }
                }
                catch { Thread.Sleep(100); }
            }
        }

        static void ProcessarComandoDebug(string input)
        {
            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            switch (parts[0].ToLowerInvariant())
            {
                case "evento" when parts.Length >= 2:
                    if (Enum.TryParse<TipoEvento>(parts[1].ToLowerInvariant(), out var tipo))
                    {
                        double intens = parts.Length >= 3 &&
                                        double.TryParse(parts[2], NumberStyles.Any,
                                            CultureInfo.InvariantCulture, out double x)
                                        ? x : 1.0;
                        IniciarEvento(tipo, intens);
                    }
                    else
                        RegistarLog($"[CMD] '{parts[1]}' desconhecido. Tipos: incendio|multidao|tempestade|transito|smog|construcao|chuva");
                    break;

                case "limpar":
                    LimparEventos();
                    break;

                case "status":
                    RegistarLog($"[STATUS] Zona={_zona} ({_zonaType}) | {StatusEventos()}");
                    break;

                case "help":
                case "ajuda":
                    RegistarLog("[CMD] evento <tipo> [0.1-2.0]   limpar   status   help");
                    RegistarLog("[CMD] incendio | multidao | tempestade | transito | smog | construcao | chuva");
                    break;

                default:
                    RegistarLog($"[CMD] '{parts[0]}' desconhecido. Escreva 'help'.");
                    break;
            }
        }

        #endregion
    }
}
