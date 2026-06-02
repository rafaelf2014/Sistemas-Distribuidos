using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace sensor
{
    partial class Program
    {
        #region GERAÇÃO DE DADOS

        // Random.Shared is thread-safe (.NET 6+); safe for concurrent timer threads
        // Ornstein-Uhlenbeck drift state per tipo — ConcurrentDictionary avoids init race
        private static readonly ConcurrentDictionary<string, double> _drift = new();

        // ── Zone profiles ─────────────────────────────────────────────
        private enum ZoneTipo { Residencial, Comercial, Industrial, Parque, Trafego }

        private record ZoneProfile(
            double BaseTEMP,  double BaseHUM,   double BaseCO2,
            double BaseRUIDO, double BaseLUMIN, double BasePART,
            double BaseNO2,   double BaseO3,    double BaseWIND
        );

        // Baseline values per zone type for all 9 sensor types
        private static readonly Dictionary<ZoneTipo, ZoneProfile> _profiles = new()
        {
            // TEMP   HUM   CO2   RUIDO  LUMIN   PART  NO2   O3   WIND
            [ZoneTipo.Residencial] = new(17,  62,  420,  42, 22000, 10,  20,  25,  8),
            [ZoneTipo.Comercial]   = new(20,  52,  620,  60, 30000, 22,  45,  35, 10),
            [ZoneTipo.Industrial]  = new(23,  45,  850,  68, 15000, 55,  85,  15, 12),
            [ZoneTipo.Parque]      = new(15,  68,  380,  38, 42000,  6,  15,  40, 15),
            [ZoneTipo.Trafego]     = new(19,  50,  720,  72, 28000, 35, 110,  30,  9),
        };

        private static ZoneTipo ObterZonaTipo() => (_zonaType ?? "residencial").ToLowerInvariant() switch
        {
            "comercial"  => ZoneTipo.Comercial,
            "industrial" => ZoneTipo.Industrial,
            "parque"     => ZoneTipo.Parque,
            "trafego"    => ZoneTipo.Trafego,
            _            => ZoneTipo.Residencial,
        };

        // Box-Muller Gaussian noise
        private static double Gauss()
        {
            double u1 = Math.Max(1e-10, Random.Shared.NextDouble());
            double u2 = Random.Shared.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        // ── Multi-layer baseline ───────────────────────────────────────
        // Layers: 24h circadian + 4h occupancy cycle + 0.5h micro-variation
        //       + Ornstein-Uhlenbeck drift + Gaussian noise
        private static double GerarBaseline(string tipo)
        {
            var    p    = _profiles[ObterZonaTipo()];
            double h    = (DateTime.Now.TimeOfDay.TotalSeconds % 7200.0) / 7200.0 * 24.0;

            double sin24 = Math.Sin(2 * Math.PI / 24.0 * (h - 6));
            double sin4  = Math.Sin(2 * Math.PI /  4.0 * h);
            double sin05 = Math.Sin(2 * Math.PI /  0.5 * h);

            double prev = _drift.GetOrAdd(tipo, 0.0);
            _drift[tipo] = prev * 0.92 + (Random.Shared.NextDouble() * 2 - 1);

            double d = _drift[tipo];
            double g = Gauss();

            double v = tipo switch
            {
                "TEMP"  => p.BaseTEMP  + 7.0  * sin24                 + 1.8 * sin4  + 0.6 * sin05 + g * 0.4  + d * 0.9,
                "HUM"   => p.BaseHUM   - 14.0 * sin24                 + 2.5 * sin4  + 0.8 * sin05 + g * 0.7  + d * 1.2,
                "CO2"   => p.BaseCO2   + 180.0 * sin24                + 65.0 * sin4 + 22.0 * sin05 + g * 9   + d * 18,
                "RUIDO" => p.BaseRUIDO + 10.0 * Math.Abs(sin24)       + 7.0 * Math.Abs(sin4) + 2.5 * sin05 + g * 1.5 + d * 2.5,
                "LUMIN" => Math.Max(0, p.BaseLUMIN * Math.Max(0, sin24) + g * 250 + d * 350),
                "PART"  => Math.Max(0, p.BasePART  + 7.0 * sin24     + 2.5 * sin4  + g * 1.2 + d * 2),
                "NO2"   => Math.Max(0, p.BaseNO2   + 22.0 * Math.Abs(sin24) + 9.0 * Math.Abs(sin4) + g * 3  + d * 4.5),
                "O3"    => Math.Max(0, p.BaseO3    + 14.0 * Math.Max(0, sin24) + 4.0 * sin4        + g * 2  + d * 3),
                "WIND"  => Math.Max(0, p.BaseWIND  + 4.5 * sin24 + 2.0 * (Random.Shared.NextDouble() * 2 - 1) + Math.Abs(g) * 1.5),
                _       => 50 + g
            };

            return v;
        }

        // ── Correlated events ──────────────────────────────────────────
        internal enum TipoEvento { incendio, multidao, tempestade, transito, smog, construcao, chuva }

        private class Evento
        {
            public TipoEvento Tipo;
            public int        Passo;
            public int        TotalPassos;
            public double     Intensidade;
        }

        private static readonly List<Evento> _eventosAtivos = new();
        private static readonly object       _eventosLock   = new();

        // Per-event deltas on top of baseline (positive = increase, negative = decrease)
        private static readonly Dictionary<TipoEvento, Dictionary<string, double>> _eventImpact = new()
        {
            [TipoEvento.incendio]   = new() { ["TEMP"]=22,  ["CO2"]=900, ["PART"]=200, ["NO2"]=55,  ["O3"]=35, ["RUIDO"]=8               },
            [TipoEvento.multidao]   = new() { ["RUIDO"]=28, ["CO2"]=240, ["TEMP"]=2,   ["PART"]=22, ["NO2"]=14                            },
            [TipoEvento.tempestade] = new() { ["WIND"]=45,  ["HUM"]=28,  ["LUMIN"]=-18000, ["TEMP"]=-4, ["PART"]=-7, ["O3"]=4            },
            [TipoEvento.transito]   = new() { ["NO2"]=75,   ["RUIDO"]=25, ["CO2"]=190, ["PART"]=28, ["O3"]=12                             },
            [TipoEvento.smog]       = new() { ["PART"]=110, ["NO2"]=48,  ["O3"]=30,    ["LUMIN"]=-12000, ["HUM"]=-5                       },
            [TipoEvento.construcao] = new() { ["RUIDO"]=32, ["PART"]=85, ["NO2"]=18,   ["WIND"]=-3                                        },
            [TipoEvento.chuva]      = new() { ["HUM"]=24,   ["PART"]=-9, ["LUMIN"]=-9000, ["TEMP"]=-2, ["WIND"]=7, ["O3"]=-4             },
        };

        private static double CalcularImpactoEventos(string tipo)
        {
            double total = 0;
            lock (_eventosLock)
            {
                foreach (var ev in _eventosAtivos)
                {
                    if (!_eventImpact[ev.Tipo].TryGetValue(tipo, out double impact)) continue;
                    // Smooth half-sine envelope: rises to peak then returns to zero
                    double envelope = ev.Intensidade * Math.Sin(Math.PI * ev.Passo / ev.TotalPassos);
                    total += impact * envelope;
                }
            }
            return total;
        }

        // Time-gated tick: CAS ensures exactly one thread wins per second
        private static long _ultimoTickEventoTicks = 0;

        private static void TickEventos()
        {
            long now    = DateTime.Now.Ticks;
            long ultimo = _ultimoTickEventoTicks;
            if (now - ultimo < TimeSpan.TicksPerSecond) return;
            if (Interlocked.CompareExchange(ref _ultimoTickEventoTicks, now, ultimo) != ultimo) return;

            TipoEvento? autoEvento      = null;
            double      autoIntensidade = 0;

            lock (_eventosLock)
            {
                for (int i = _eventosAtivos.Count - 1; i >= 0; i--)
                {
                    _eventosAtivos[i].Passo++;
                    if (_eventosAtivos[i].Passo >= _eventosAtivos[i].TotalPassos)
                        _eventosAtivos.RemoveAt(i);
                }

                // 2% auto-trigger chance per second
                if (Random.Shared.Next(100) < 2)
                {
                    var valores = Enum.GetValues<TipoEvento>();
                    autoEvento      = (TipoEvento)valores.GetValue(Random.Shared.Next(valores.Length))!;
                    autoIntensidade = 0.5 + Random.Shared.NextDouble() * 0.5;
                }
            }

            // IniciarEvento called outside _eventosLock to avoid lock-order inversion
            if (autoEvento.HasValue)
                IniciarEvento(autoEvento.Value, autoIntensidade);
        }

        internal static void IniciarEvento(TipoEvento tipo, double intensidade = 1.0)
        {
            int passos;
            lock (_eventosLock)
            {
                passos = Random.Shared.Next(8, 20);
                _eventosAtivos.Add(new Evento
                {
                    Tipo        = tipo,
                    Passo       = 0,
                    TotalPassos = passos,
                    Intensidade = Math.Clamp(intensidade, 0.1, 2.0),
                });
            }
            RegistarLog($"[EVENTO] {tipo} iniciado — duração ~{passos}t, intensidade {intensidade:F1}x");
        }

        internal static void LimparEventos()
        {
            lock (_eventosLock) { _eventosAtivos.Clear(); }
            RegistarLog("[EVENTO] Todos os eventos cancelados.");
        }

        internal static string StatusEventos()
        {
            lock (_eventosLock)
            {
                if (_eventosAtivos.Count == 0) return "nenhum evento ativo";
                return string.Join(", ", _eventosAtivos.Select(e =>
                    $"{e.Tipo}({e.Passo}/{e.TotalPassos} {e.Intensidade:F1}x)"));
            }
        }

        static async Task GerarEEnviarDado(SensorConfig cfg)
        {
            if (!_isOnline) return;

            string tipo = cfg.TipoDado;

            TickEventos();
            double baseline = GerarBaseline(tipo);
            double delta    = CalcularImpactoEventos(tipo);
            double v        = Math.Round(baseline + delta, 1);

            string ts  = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            string val = v.ToString(CultureInfo.InvariantCulture);

            string evTag = "";
            lock (_eventosLock)
            {
                var ev = _eventosAtivos.FirstOrDefault(e =>
                    _eventImpact[e.Tipo].ContainsKey(tipo));
                if (ev != null) evTag = $" [{ev.Tipo}]";
            }

            RegistarLog($"{tipo}: {val}{evTag}");
            await Publicar($"DATA_SEND|{_idSensor}|{tipo}|{val}|{ts}", $"{_zona}.{tipo}");
        }

        #endregion
    }
}
