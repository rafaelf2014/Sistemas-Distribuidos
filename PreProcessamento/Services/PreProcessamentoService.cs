using Grpc.Core;
using PreProcessamento;

namespace PreProcessamento.Services
{
    public class NormalizacaoService : PreProcessamentoService.PreProcessamentoServiceBase
    {
        private static readonly Dictionary<string, (double Min, double Max)> _intervalos = new()
        {
            ["TEMP"]  = (-50,    80),
            ["HUM"]   = (  0,   100),
            ["CO2"]   = (  0,  5000),
            ["RUIDO"] = (  0,   140),
            ["LUMIN"] = (  0, 100000),
            ["PART"]  = (  0,   500),
            ["NO2"]   = (  0,   500),
            ["O3"]    = (  0,   300),
            ["WIND"]  = (  0,   200),
        };

        private static readonly Dictionary<string, string> _unidadesPadrao = new()
        {
            ["TEMP"]  = "C",
            ["HUM"]   = "%",
            ["CO2"]   = "ppm",
            ["RUIDO"] = "dB",
            ["LUMIN"] = "lux",
            ["PART"]  = "µg/m³",
            ["NO2"]   = "µg/m³",
            ["O3"]    = "ppb",
            ["WIND"]  = "km/h",
        };

        // Minimum range (max-min) below which a batch is considered suspiciously stable
        private static double GetStuckThreshold(string tipo) => tipo switch
        {
            "TEMP"  => 0.05,
            "HUM"   => 0.20,
            "CO2"   => 2.00,
            "RUIDO" => 0.50,
            "LUMIN" => 10.0,
            "PART"  => 0.20,
            "NO2"   => 0.20,
            "O3"    => 0.20,
            "WIND"  => 0.10,
            _       => 0.10,
        };

        private record BatchFlags(bool IsBloqueado, bool IsSuspeito);
        private record TsFlags(bool Futuro, bool Antigo, bool Desordem);

        public override Task<BlocoLeiturasProcessadas> NormalizarBloco(BlocoLeiturasBrutas request, ServerCallContext context)
        {
            // Pass 1: unit conversion
            var provisorios = request.Dados
                .Select(l => { var v = Converter(l); return (Leitura: l, Valor: v, Convertido: Math.Abs(v - l.Valor) > 0.001); })
                .ToList();

            // Timestamp ordering + plausibility per reading
            DateTime now    = DateTime.UtcNow;
            DateTime? prevTs = null;
            var tsFlags = provisorios.Select(p =>
            {
                bool parsed   = DateTime.TryParse(p.Leitura.Timestamp, out DateTime ts);
                bool futuro   = parsed && ts > now.AddSeconds(60);
                bool antigo   = parsed && ts < now.AddMinutes(-30);
                bool desordem = parsed && prevTs.HasValue && ts < prevTs.Value;
                if (parsed) prevTs = ts;
                return new TsFlags(futuro, antigo, desordem);
            }).ToList();

            // Collect in-range values sorted for stuck-sensor range check
            var valoresValidos = provisorios
                .Where(p => _intervalos.TryGetValue(p.Leitura.Tipo.ToUpper(), out var iv)
                            && p.Valor >= iv.Min && p.Valor <= iv.Max)
                .Select(p => p.Valor)
                .OrderBy(v => v)
                .ToList();

            bool temStats = valoresValidos.Count >= 4;

            // Stuck sensor: check range of in-range values
            bool isBloqueado = false, isSuspeito = false;
            if (temStats)
            {
                double range = valoresValidos.Last() - valoresValidos.First();
                string tipo  = provisorios[0].Leitura.Tipo.ToUpper();
                isBloqueado  = range < 0.001;
                isSuspeito   = !isBloqueado && range < GetStuckThreshold(tipo);
            }

            var resposta = new BlocoLeiturasProcessadas();
            for (int i = 0; i < provisorios.Count; i++)
            {
                var (leitura, valor, convertido) = provisorios[i];
                resposta.Dados.Add(Processar(leitura, valor, convertido,
                    new BatchFlags(isBloqueado, isSuspeito), tsFlags[i]));
            }
            return Task.FromResult(resposta);
        }

        public override Task<LeituraProcessada> Normalizar(LeituraBruta request, ServerCallContext context)
        {
            double valor      = Converter(request);
            bool   convertido = Math.Abs(valor - request.Valor) > 0.001;

            DateTime now  = DateTime.UtcNow;
            bool parsed   = DateTime.TryParse(request.Timestamp, out DateTime ts);
            var tsf = new TsFlags(
                Futuro:   parsed && ts > now.AddSeconds(60),
                Antigo:   parsed && ts < now.AddMinutes(-30),
                Desordem: false
            );

            return Task.FromResult(Processar(request, valor, convertido,
                new BatchFlags(false, false), tsf));
        }

        private static double Converter(LeituraBruta request)
        {
            double valor   = request.Valor;
            string unidade = request.Unidade.Trim();
            switch (request.Tipo.ToUpper())
            {
                case "TEMP":
                    if      (unidade == "F") valor = (valor - 32) * 5.0 / 9.0;
                    else if (unidade == "K") valor = valor - 273.15;
                    break;
                case "PART":
                    if (unidade == "mg/m³") valor = valor * 1000.0;
                    break;
                case "LUMIN":
                    if (unidade == "fc") valor = valor * 10.7639;
                    break;
            }
            return valor;
        }

        private static LeituraProcessada Processar(
            LeituraBruta leitura, double valor, bool convertido,
            BatchFlags bf, TsFlags tf)
        {
            string tipo     = leitura.Tipo.ToUpper();
            bool   valido   = true;
            float  qualidade = 1.0f;
            var    obs      = new System.Text.StringBuilder();

            void Anotar(string msg)
            {
                if (obs.Length > 0) obs.Append("; ");
                obs.Append(msg);
            }

            if (convertido) qualidade *= 0.98f;

            // Range validation
            if (_intervalos.TryGetValue(tipo, out var intervalo))
            {
                if (valor < intervalo.Min || valor > intervalo.Max)
                {
                    valido    = false;
                    qualidade = 0.0f;
                    Anotar($"Valor {Math.Round(valor, 2)} fora de [{intervalo.Min},{intervalo.Max}]");
                }
                else
                {
                    double pos = (valor - intervalo.Min) / (intervalo.Max - intervalo.Min);
                    if (Math.Abs(pos - 0.5) > 0.425) qualidade -= 0.10f;
                }
            }

            if (valido)
            {
                // Stuck sensor
                if (bf.IsBloqueado)
                {
                    qualidade *= 0.40f;
                    Anotar("Sensor bloqueado — variância zero");
                }
                else if (bf.IsSuspeito)
                {
                    qualidade *= 0.75f;
                    Anotar("Variância suspeita no batch");
                }

                // Timestamp issues
                if (tf.Futuro)   { qualidade *= 0.80f; Anotar("Timestamp futuro");         }
                if (tf.Antigo)   { qualidade *= 0.85f; Anotar("Leitura atrasada (>30min)"); }
                if (tf.Desordem) { qualidade *= 0.90f; Anotar("Desordem temporal");         }
            }

            qualidade = Math.Max(0.0f, Math.Min(1.0f, qualidade));

            return new LeituraProcessada
            {
                GatewayId        = leitura.GatewayId,
                SensorId         = leitura.SensorId,
                Zona             = leitura.Zona,
                Tipo             = tipo,
                ValorNormalizado = Math.Round(valor, 2),
                UnidadePadrao    = _unidadesPadrao.GetValueOrDefault(tipo, leitura.Unidade),
                Timestamp        = leitura.Timestamp,
                Valido           = valido,
                Observacao       = obs.ToString(),
                Qualidade        = qualidade
            };
        }
    }
}
