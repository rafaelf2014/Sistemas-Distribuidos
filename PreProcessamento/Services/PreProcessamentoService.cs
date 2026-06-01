using Grpc.Core;
using PreProcessamento;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;

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
        private record ResolvedLeitura(LeituraBruta Leitura, bool Valido, string Erro = "");

        // ── Format detection ──────────────────────────────────────────────────────

        private static ResolvedLeitura ResolverPayload(LeituraBruta l)
        {
            if (string.IsNullOrEmpty(l.PayloadRede))
                return new ResolvedLeitura(l, true);

            if (!TryParsarFormato(l.PayloadRede,
                    out string sensorId, out string tipo, out double valor,
                    out string unidade, out string timestamp))
                return new ResolvedLeitura(l, false, "Formato de payload desconhecido ou malformado");

            return new ResolvedLeitura(new LeituraBruta
            {
                GatewayId = l.GatewayId,
                SensorId  = sensorId,
                Zona      = l.Zona,
                Tipo      = tipo,
                Valor     = valor,
                Unidade   = unidade,
                Timestamp = timestamp,
            }, true);
        }

        // Detects format (JSON / XML / QueryString / Hex) and extracts structured fields.
        // Pipe format (DATA_SEND|...) is handled by the Gateway before reaching here.
        private static bool TryParsarFormato(string raw,
            out string sensorId, out string tipo, out double valor,
            out string unidade, out string timestamp)
        {
            sensorId  = "";
            tipo      = "";
            valor     = 0;
            unidade   = "";
            timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

            try
            {
                string t = raw.TrimStart();

                // JSON: {"sensor_id":"S001","tipo_dado":"TEMP","valor":20.5,"unidade":"C","timestamp":"..."}
                if (t.StartsWith("{"))
                {
                    using var doc = JsonDocument.Parse(t);
                    var root = doc.RootElement;
                    sensorId  = root.TryGetProperty("sensor_id", out var s)   ? s.GetString()   ?? "" : "";
                    tipo      = root.TryGetProperty("tipo_dado", out var td)  ? td.GetString()  ?? "" :
                                root.TryGetProperty("tipo",      out var t2)  ? t2.GetString()  ?? "" : "";
                    valor     = root.TryGetProperty("valor",     out var v)   ? v.GetDouble()   : 0;
                    unidade   = root.TryGetProperty("unidade",   out var u)   ? u.GetString()   ?? "" : "";
                    timestamp = root.TryGetProperty("timestamp", out var ts)  ? ts.GetString()  ?? timestamp : timestamp;
                    tipo      = tipo.ToUpper();
                    return sensorId.Length > 0 && tipo.Length > 0;
                }

                // XML: <leitura><SensorId>S001</SensorId><Variavel>TEMP</Variavel><Valor>20.5</Valor><Unidade>C</Unidade><DataHora>...</DataHora></leitura>
                if (t.StartsWith("<"))
                {
                    var xml = XDocument.Parse(t);
                    if (xml.Root == null) return false;
                    sensorId  = xml.Root.Element("SensorId")?.Value ?? "";
                    tipo      = (xml.Root.Element("Variavel")?.Value ?? "").ToUpper();
                    double.TryParse(xml.Root.Element("Valor")?.Value ?? "0",
                        NumberStyles.Any, CultureInfo.InvariantCulture, out valor);
                    unidade   = xml.Root.Element("Unidade")?.Value  ?? "";
                    timestamp = xml.Root.Element("DataHora")?.Value ?? timestamp;
                    return sensorId.Length > 0 && tipo.Length > 0;
                }

                // QueryString: id=S001&tipo=TEMP&val=20.5&unidade=C&ts=...
                if (t.Contains('=') && !t.Contains('|'))
                {
                    var vars = t.Split('&')
                               .Select(p => p.Split('='))
                               .Where(p => p.Length == 2)
                               .ToDictionary(p => p[0].Trim(),
                                             p => Uri.UnescapeDataString(p[1].Trim()),
                                             StringComparer.OrdinalIgnoreCase);
                    sensorId  = vars.GetValueOrDefault("id",      "");
                    tipo      = vars.GetValueOrDefault("tipo",    "").ToUpper();
                    double.TryParse(vars.GetValueOrDefault("val", "0"),
                        NumberStyles.Any, CultureInfo.InvariantCulture, out valor);
                    unidade   = vars.GetValueOrDefault("unidade", "");
                    timestamp = vars.GetValueOrDefault("ts",      timestamp);
                    return sensorId.Length > 0 && tipo.Length > 0;
                }

                // Hex: IITTPPPP[TTTTTTTT]
                // II = sensor id (byte), TT = tipo (byte), PPPP = signed value × 0.1,
                // optional TTTTTTTT = unix epoch seconds
                if (t.Length >= 8 && t.All(c => Uri.IsHexDigit(c)))
                {
                    byte  hexId   = Convert.ToByte(t.Substring(0, 2), 16);
                    byte  hexTipo = Convert.ToByte(t.Substring(2, 2), 16);
                    short hexVal  = Convert.ToInt16(t.Substring(4, 4), 16);
                    sensorId = $"S{hexId:D3}";
                    tipo = hexTipo switch
                    {
                        0x0A => "TEMP",  0x0B => "HUM",   0x0C => "CO2",
                        0x0D => "LUMIN", 0x0E => "RUIDO", 0x0F => "PART",
                        0x10 => "NO2",   0x11 => "O3",    0x12 => "WIND",
                        _ => ""
                    };
                    valor = hexVal / 10.0;
                    if (t.Length >= 16)
                    {
                        long epochSecs = Convert.ToInt64(t.Substring(8, 8), 16);
                        timestamp = DateTimeOffset.FromUnixTimeSeconds(epochSecs)
                                       .UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
                    }
                    return tipo.Length > 0;
                }
            }
            catch { }

            return false;
        }

        // ── gRPC methods ──────────────────────────────────────────────────────────

        public override Task<BlocoLeiturasProcessadas> NormalizarBloco(BlocoLeiturasBrutas request, ServerCallContext context)
        {
            // Step 0: resolve raw payloads into structured leituras (or error markers)
            var resolvidas   = request.Dados.Select(ResolverPayload).ToList();
            var estruturadas = resolvidas.Where(r => r.Valido).Select(r => r.Leitura).ToList();

            // Pass 1: unit conversion on structured items only
            var provisorios = estruturadas
                .Select(l => { var v = Converter(l); return (Leitura: l, Valor: v, Convertido: Math.Abs(v - l.Valor) > 0.001); })
                .ToList();

            // Timestamp ordering + plausibility
            DateTime now     = DateTime.UtcNow;
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

            // Stuck-sensor check on in-range structured values
            var valoresValidos = provisorios
                .Where(p => _intervalos.TryGetValue(p.Leitura.Tipo.ToUpper(), out var iv)
                            && p.Valor >= iv.Min && p.Valor <= iv.Max)
                .Select(p => p.Valor)
                .OrderBy(v => v)
                .ToList();

            bool isBloqueado = false, isSuspeito = false;
            if (valoresValidos.Count >= 4)
            {
                double range = valoresValidos.Last() - valoresValidos.First();
                string tipo  = provisorios[0].Leitura.Tipo.ToUpper();
                isBloqueado  = range < 0.001;
                isSuspeito   = !isBloqueado && range < GetStuckThreshold(tipo);
            }

            // Build response preserving original request order
            var resposta = new BlocoLeiturasProcessadas();
            int pIdx = 0;
            foreach (var r in resolvidas)
            {
                if (!r.Valido)
                {
                    resposta.Dados.Add(new LeituraProcessada
                    {
                        GatewayId  = r.Leitura.GatewayId,
                        Zona       = r.Leitura.Zona,
                        Valido     = false,
                        Observacao = r.Erro,
                        Qualidade  = 0f
                    });
                }
                else
                {
                    var (leitura, valor, convertido) = provisorios[pIdx];
                    resposta.Dados.Add(Processar(leitura, valor, convertido,
                        new BatchFlags(isBloqueado, isSuspeito), tsFlags[pIdx]));
                    pIdx++;
                }
            }
            return Task.FromResult(resposta);
        }

        public override Task<LeituraProcessada> Normalizar(LeituraBruta request, ServerCallContext context)
        {
            var resolved = ResolverPayload(request);
            if (!resolved.Valido)
                return Task.FromResult(new LeituraProcessada
                {
                    GatewayId  = request.GatewayId,
                    Zona       = request.Zona,
                    Valido     = false,
                    Observacao = resolved.Erro,
                    Qualidade  = 0f
                });

            var leitura     = resolved.Leitura;
            double valor    = Converter(leitura);
            bool convertido = Math.Abs(valor - leitura.Valor) > 0.001;

            DateTime now = DateTime.UtcNow;
            bool parsed  = DateTime.TryParse(leitura.Timestamp, out DateTime ts);
            var tsf = new TsFlags(
                Futuro:   parsed && ts > now.AddSeconds(60),
                Antigo:   parsed && ts < now.AddMinutes(-30),
                Desordem: false
            );

            return Task.FromResult(Processar(leitura, valor, convertido,
                new BatchFlags(false, false), tsf));
        }

        // ── Internal helpers ──────────────────────────────────────────────────────

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
            string tipo      = leitura.Tipo.ToUpper();
            bool   valido    = true;
            float  qualidade = 1.0f;
            var    obs       = new System.Text.StringBuilder();

            void Anotar(string msg)
            {
                if (obs.Length > 0) obs.Append("; ");
                obs.Append(msg);
            }

            if (convertido) qualidade *= 0.98f;

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

                if (tf.Futuro)   { qualidade *= 0.80f; Anotar("Timestamp futuro");           }
                if (tf.Antigo)   { qualidade *= 0.85f; Anotar("Leitura atrasada (>30min)");  }
                if (tf.Desordem) { qualidade *= 0.90f; Anotar("Desordem temporal");           }
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
