using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace sensor
{
    partial class Sensor
    {
        #region GERAÇÃO DE DADOS

        // Random.Shared is thread-safe (.NET 6+); safe for concurrent timer threads
        // Ornstein-Uhlenbeck drift state per tipo — ConcurrentDictionary avoids init race
        private readonly ConcurrentDictionary<string, double> _drift = new();

        // ── Zone profiles ─────────────────────────────────────────────
        private enum ZoneTipo { Residencial, Comercial, Industrial, Parque, Trafego }

        private record ZoneProfile(
            double BaseTEMP,  double BaseHUM,   double BaseCO2,
            double BaseRUIDO, double BaseLUMIN, double BasePART,
            double BaseNO2,   double BaseO3,    double BaseWIND
        );

        // Baseline values per zone type for all 9 sensor types
        private readonly Dictionary<ZoneTipo, ZoneProfile> _profiles = new()
        {
            // TEMP   HUM   CO2   RUIDO  LUMIN   PART  NO2   O3   WIND
            [ZoneTipo.Residencial] = new(17,  62,  420,  42, 22000, 10,  20,  25,  8),
            [ZoneTipo.Comercial]   = new(20,  52,  620,  60, 30000, 22,  45,  35, 10),
            [ZoneTipo.Industrial]  = new(23,  45,  850,  68, 15000, 55,  85,  15, 12),
            [ZoneTipo.Parque]      = new(15,  68,  380,  38, 42000,  6,  15,  40, 15),
            [ZoneTipo.Trafego]     = new(19,  50,  720,  72, 28000, 35, 110,  30,  9),
        };

        private ZoneTipo ObterZonaTipo() => (_zonaType ?? "residencial").ToLowerInvariant() switch
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
        private double GerarBaseline(string tipo)
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

        private readonly List<Evento> _eventosAtivos = new();
        private readonly object       _eventosLock   = new();

        // Per-event deltas on top of baseline (positive = increase, negative = decrease)
        private readonly Dictionary<TipoEvento, Dictionary<string, double>> _eventImpact = new()
        {
            [TipoEvento.incendio]   = new() { ["TEMP"]=22,  ["CO2"]=900, ["PART"]=200, ["NO2"]=55,  ["O3"]=35, ["RUIDO"]=8               },
            [TipoEvento.multidao]   = new() { ["RUIDO"]=28, ["CO2"]=240, ["TEMP"]=2,   ["PART"]=22, ["NO2"]=14                            },
            [TipoEvento.tempestade] = new() { ["WIND"]=45,  ["HUM"]=28,  ["LUMIN"]=-18000, ["TEMP"]=-4, ["PART"]=-7, ["O3"]=4            },
            [TipoEvento.transito]   = new() { ["NO2"]=75,   ["RUIDO"]=25, ["CO2"]=190, ["PART"]=28, ["O3"]=12                             },
            [TipoEvento.smog]       = new() { ["PART"]=110, ["NO2"]=48,  ["O3"]=30,    ["LUMIN"]=-12000, ["HUM"]=-5                       },
            [TipoEvento.construcao] = new() { ["RUIDO"]=32, ["PART"]=85, ["NO2"]=18,   ["WIND"]=-3                                        },
            [TipoEvento.chuva]      = new() { ["HUM"]=24,   ["PART"]=-9, ["LUMIN"]=-9000, ["TEMP"]=-2, ["WIND"]=7, ["O3"]=-4             },
        };

        private double CalcularImpactoEventos(string tipo)
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
        private long _ultimoTickEventoTicks = 0;

        private void TickEventos()
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

        internal void IniciarEvento(TipoEvento tipo, double intensidade = 1.0)
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

        internal void LimparEventos()
        {
            lock (_eventosLock) { _eventosAtivos.Clear(); }
            RegistarLog("[EVENTO] Todos os eventos cancelados.");
        }

        internal string StatusEventos()
        {
            lock (_eventosLock)
            {
                if (_eventosAtivos.Count == 0) return "nenhum evento ativo";
                return string.Join(", ", _eventosAtivos.Select(e =>
                    $"{e.Tipo}({e.Passo}/{e.TotalPassos} {e.Intensidade:F1}x)"));
            }
        }

        // ── Unit conversion (standard → declared unit) ──────────────────
        // Inverse of PreProcessamento.Converter: the sensor simulates a device that
        // natively reports in a non-standard unit, so PreProcessamento can convert it back.
        private static double ConverterParaUnidade(double valor, string tipo, string unidade) =>
            (tipo, unidade) switch
            {
                ("TEMP",  "F")     => valor * 9.0 / 5.0 + 32.0,
                ("TEMP",  "K")     => valor + 273.15,
                ("PART",  "mg/m³") => valor / 1000.0,
                ("LUMIN", "fc")    => valor / 10.7639,
                _                  => valor
            };

        // ── Wire-format encoder ─────────────────────────────────────────
        // Builds the payload in the format selected by config (_formato).
        // pipe → handled directly by the Gateway; the other 4 go through PreProcessamento.
        private string FormatarLeitura(string tipo, double valor, string unidade, DateTime ts)
        {
            string val   = valor.ToString(CultureInfo.InvariantCulture);
            string tsStr = ts.ToString("yyyy-MM-ddTHH:mm:ssZ");

            switch (_formato)
            {
                case "json":
                    return $"{{\"sensor_id\":\"{_idSensor}\",\"tipo_dado\":\"{tipo}\",\"valor\":{val},\"unidade\":\"{unidade}\",\"timestamp\":\"{tsStr}\"}}";

                case "xml":
                    return $"<leitura><SensorId>{_idSensor}</SensorId><Variavel>{tipo}</Variavel><Valor>{val}</Valor><Unidade>{unidade}</Unidade><DataHora>{tsStr}</DataHora></leitura>";

                case "querystring":
                    return $"id={_idSensor}&tipo={tipo}&val={val}&unidade={unidade}&ts={tsStr}";

                case "hex":
                    return FormatarHex(tipo, valor, ts);

                case "pipe":
                default:
                    return string.IsNullOrEmpty(unidade)
                        ? $"DATA_SEND|{_idSensor}|{tipo}|{val}|{tsStr}"
                        : $"DATA_SEND|{_idSensor}|{tipo}|{val}|{tsStr}|{unidade}";
            }
        }

        // Hex frame: IITTPPPP TTTTTTTT
        //   II = sensor id byte, TT = tipo byte, PPPP = signed int16 (value × 10),
        //   TTTTTTTT = unix epoch seconds. No unit field — value stays standard.
        private string FormatarHex(string tipo, double valor, DateTime ts)
        {
            byte idByte = byte.TryParse(_idSensor.TrimStart('S', 's'), out var b) ? b : (byte)0;
            byte tipoByte = tipo switch
            {
                "TEMP" => 0x0A, "HUM" => 0x0B, "CO2"  => 0x0C, "LUMIN" => 0x0D,
                "RUIDO" => 0x0E, "PART" => 0x0F, "NO2" => 0x10, "O3"    => 0x11,
                "WIND" => 0x12, _ => 0x00
            };
            short sval     = (short)Math.Round(valor * 10.0);
            long  epoch    = ((DateTimeOffset)DateTime.SpecifyKind(ts, DateTimeKind.Utc)).ToUnixTimeSeconds();
            return $"{idByte:X2}{tipoByte:X2}{(ushort)sval:X4}{epoch:X8}";
        }

        async Task GerarEEnviarDado(SensorConfig cfg)
        {
            if (!_isOnline) return;

            string tipo = cfg.TipoDado;

            TickEventos();
            double v = Math.Round(GerarBaseline(tipo) + CalcularImpactoEventos(tipo), 1);

            DateTime ts = DateTime.UtcNow;

            // Hex carries no unit, so the value must stay in standard units.
            string unidade = _formato == "hex" ? "" : cfg.Unidade;
            double vOut    = ConverterParaUnidade(v, tipo, unidade);

            string evTag = "";
            lock (_eventosLock)
            {
                var ev = _eventosAtivos.FirstOrDefault(e =>
                    _eventImpact[e.Tipo].ContainsKey(tipo));
                if (ev != null) evTag = $" [{ev.Tipo}]";
            }

            string payload = FormatarLeitura(tipo, vOut, unidade, ts);
            RegistarLog($"{tipo}: {vOut.ToString(CultureInfo.InvariantCulture)}{unidade} [{_formato}]{evTag}");
            await Publicar(payload, $"{_zona}.{tipo}");
        }

        #endregion
    }
}
