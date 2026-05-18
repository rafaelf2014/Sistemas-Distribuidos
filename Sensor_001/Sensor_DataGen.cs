using System;
using System.Collections.Generic;
using System.Globalization;
using System.Timers;

namespace sensor
{
    partial class Program
    {
        #region GERAÇÃO DE DADOS

        private static readonly Random _rng = new();

        class AnomaliaOnda
        {
            public bool   Ativa       = false;
            public int    Passo       = 0;
            public int    TotalPassos = 0;
            public double Pico        = 0;
        }

        static readonly Dictionary<string, AnomaliaOnda> _ondas = new();

        static double GerarBaseline(string tipo)
        {
            double h     = DateTime.Now.Hour + DateTime.Now.Minute / 60.0;
            double ruido = (_rng.NextDouble() - 0.5) * 2;

            return tipo switch
            {
                "TEMP"  => 22.5 + 12.5 * Math.Sin((h - 8) * Math.PI / 12) + ruido,
                "HUM"   => 55.0 - 22.0 * Math.Sin((h - 8) * Math.PI / 12) + ruido,
                "CO2"   => 600  + 280  * Math.Sin((h - 6) * Math.PI / 12) + ruido * 10,
                "RUIDO" => 45   + 20   * Math.Sin((h - 6) * Math.PI / 12) + ruido * 2,
                "LUMIN" => Math.Max(0, 40000 * Math.Sin((h - 6) * Math.PI / 14)) + ruido * 50,
                "PART"  => 20   + 12   * Math.Sin((h - 2) * Math.PI / 12) + ruido,
                _       => 50 + ruido
            };
        }

        static double GerarPicoAnomalya(string tipo) => tipo switch
        {
            "TEMP"  => _rng.Next(15, 25),
            "HUM"   => _rng.Next(15, 30),
            "CO2"   => _rng.Next(600, 1200),
            "RUIDO" => _rng.Next(25, 50),
            "LUMIN" => _rng.Next(20000, 50000),
            "PART"  => _rng.Next(80, 200),
            _       => _rng.Next(20, 50)
        };

        static void GerarEEnviarDado(SensorConfig cfg)
        {
            if (!_isOnline) return;

            string tipo = cfg.TipoDado;
            if (!_ondas.ContainsKey(tipo)) _ondas[tipo] = new AnomaliaOnda();
            var onda = _ondas[tipo];

            // 5% de probabilidade de iniciar onda de anomalia
            if (!onda.Ativa && _rng.Next(100) < 5)
            {
                onda.Ativa       = true;
                onda.Passo       = 0;
                onda.TotalPassos = _rng.Next(6, 13);
                onda.Pico        = GerarPicoAnomalya(tipo);
            }

            double baseline = GerarBaseline(tipo);
            double extra    = 0;

            if (onda.Ativa)
            {
                // meia-onda sinusoidal: sobe ao pico e desce a 0
                extra = onda.Pico * Math.Sin(Math.PI * onda.Passo / onda.TotalPassos);
                onda.Passo++;
                if (onda.Passo >= onda.TotalPassos) onda.Ativa = false;
            }

            double v   = Math.Round(baseline + extra, 1);
            string ts  = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            string val = v.ToString(CultureInfo.InvariantCulture);
            string tag = extra > 0 ? " [ONDA]" : "";

            RegistarLog($"{tipo}: {val}{tag}");
            Publicar($"DATA_SEND|{_idSensor}|{tipo}|{val}|{ts}", $"{_zona}.{tipo}");
        }

        #endregion
    }
}
