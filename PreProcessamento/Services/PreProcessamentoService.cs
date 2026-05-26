using Grpc.Core;
using PreProcessamento;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Globalization;
using System.Xml.Linq;
using System.Linq;
using System.Threading.Tasks;

namespace PreProcessamento.Services
{
    public class SensorPayload
    {
        public string comando { get; set; } = "";
        public string sensor_id { get; set; } = "";
        public string tipo_dado { get; set; } = "";
        public double valor { get; set; }
        public string unidade { get; set; } = ""; 
        public string timestamp { get; set; } = "";
    }

    // Serviço gRPC de pré-processamento de dados.
    // Invocado pelo Gateway antes de escrever cada leitura no buffer CSV.
    // Responsabilidades: conversão de unidades, validação de intervalos.
    public class NormalizacaoService : PreProcessamentoService.PreProcessamentoServiceBase
    {
        // Intervalos válidos por tipo de dado (unidade canónica)
        private static readonly Dictionary<string, (double Min, double Max)> _intervalos = new()
        {
            ["TEMP"]  = (-50,  80),
            ["HUM"]   = (  0, 100),
            ["CO2"]   = (  0, 5000),
            ["RUIDO"] = (  0, 140),
            ["LUMIN"] = (  0, 100000),
            ["PART"]  = (  0, 500),
        };

        // Unidade canónica por tipo
        private static readonly Dictionary<string, string> _unidadesPadrao = new()
        {
            ["TEMP"]  = "C",
            ["HUM"]   = "%",
            ["CO2"]   = "ppm",
            ["RUIDO"] = "dB",
            ["LUMIN"] = "lux",
            ["PART"]  = "µg/m³",
        };

        public override Task<BlocoLeiturasProcessadas> NormalizarBloco(BlocoLeiturasBrutas request, ServerCallContext context)
        {
            var resposta = new BlocoLeiturasProcessadas();
            foreach (var leitura in request.Dados)
                resposta.Dados.Add(Processar(leitura));
            return Task.FromResult(resposta);
        }

        public override Task<LeituraProcessada> Normalizar(LeituraBruta request, ServerCallContext context)
            => Task.FromResult(Processar(request));

        private static LeituraProcessada Processar(LeituraBruta request)
        {
            string textoCru = request.PayloadRede.Trim();
            
            string idSensor = "";
            string tipo = "";
            double valor = 0;
            string unidade = "";
            string ts = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            bool formatoReconhecido = false;
            string obs = "";

            try
            {
                switch(textoCru)
                {
                    // Json
                    case var t when t.StartsWith("{"):
                        var dadosJson = JsonSerializer.Deserialize<SensorPayload>(t);
                        if (dadosJson != null && dadosJson.comando == "DATA_SEND")
                        {
                            idSensor = dadosJson.sensor_id;
                            tipo = dadosJson.tipo_dado;
                            valor = dadosJson.valor;
                            unidade = dadosJson.unidade ?? "";
                            ts = dadosJson.timestamp;
                            formatoReconhecido = true;
                        }
                        break;
                    //XMl        
                    case var t when t.StartsWith("<"):
                        var xml = XDocument.Parse(t);
                        if (xml.Root != null && xml.Root.Attribute("tipo")?.Value == "DATA_SEND")
                        {
                            idSensor = xml.Root.Element("SensorId")?.Value ?? "";
                            tipo = xml.Root.Element("Variavel")?.Value ?? "";
                            valor = double.Parse(xml.Root.Element("Valor")?.Value ?? "0", CultureInfo.InvariantCulture);
                            ts = xml.Root.Element("DataHora")?.Value ?? ts;
                            formatoReconhecido = true;
                        }
                        break;
                    // Query STRING
                    case var t when t.Contains("cmd="):
                        var variaveis = t.Split('&')
                                         .Select(p => p.Split('='))
                                         .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");
                        
                        if (variaveis.ContainsKey("cmd") && variaveis["cmd"] == "DATA_SEND")
                        {
                            idSensor = variaveis.GetValueOrDefault("id", "");
                            tipo = variaveis.GetValueOrDefault("tipo", "");
                            valor = double.Parse(variaveis.GetValueOrDefault("val", "0"), CultureInfo.InvariantCulture);
                            ts = variaveis.GetValueOrDefault("ts", ts);
                            formatoReconhecido = true;
                        }
                        break;
                    // STRING DELIMITADA POR PIPES
                    case var t when t.Contains("|"):
                        string[] partesPipe = t.Split('|');
                        if (partesPipe.Length >= 5 && partesPipe[0] == "DATA_SEND")
                        {
                            idSensor = partesPipe[1];
                            tipo = partesPipe[2];
                            valor = double.Parse(partesPipe[3], CultureInfo.InvariantCulture);
                            ts = partesPipe[4];
                            formatoReconhecido = true; 
                        }
                        break;
                    // HEXADECIMAL
                    case var t when t.Length <= 10 && !t.Contains(" ") && !t.Contains("|") && !t.Contains(","):
                        byte hexId = Convert.ToByte(t.Substring(0, 2), 16);
                        idSensor = $"S{hexId:D3}"; 

                        byte hexTipo = Convert.ToByte(t.Substring(2, 2), 16);
                        tipo = hexTipo switch {
                            0x0A => "TEMP",
                            0x0B => "HUM",
                            0x0C => "CO2",
                            0x0D => "LUMIN",
                            _    => "DESCONHECIDO"
                        };

                        short valCurto = Convert.ToInt16(t.Substring(4, 4), 16);
                        valor = valCurto / 10.0; 
                        
                        ts = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                        formatoReconhecido = true;
                        break;
                }
            }
            catch (Exception ex)
            {
                return new LeituraProcessada { Valido = false, Observacao = $"Erro ao descodificar formato: {ex.Message}" };
            }

            if (!formatoReconhecido)
            {
                return new LeituraProcessada { Valido = false, Observacao = "Formato de rede desconhecido ou malformado." };
            }
            
            // Converter para unidade canónica antes de validar
            unidade = unidade.Trim();
            switch (tipo.ToUpper())
            {
                case "TEMP":
                    if (unidade == "F")
                        valor = (valor - 32) * 5.0 / 9.0;
                    else if (unidade == "K")
                        valor = valor - 273.15;
                    break;
                case "PART":
                    if (unidade == "mg/m³")
                        valor = valor * 1000.0;
                    break;
                case "LUMIN":
                    if (unidade == "fc")
                        valor = valor * 10.7639;
                    break;
            }

            // Validar intervalo usando a variável local "tipo"
            bool valido = true;
            if (_intervalos.TryGetValue(tipo.ToUpper(), out var intervalo))
            {
                if (valor < intervalo.Min || valor > intervalo.Max)
                {
                    valido = false;
                    obs    = $"Valor {Math.Round(valor, 2)} fora do intervalo [{intervalo.Min}, {intervalo.Max}] para {tipo}";
                }
            }

            return new LeituraProcessada
            {
                GatewayId        = request.GatewayId,
                SensorId         = idSensor, // Usa as vars extraídas do switch
                Zona             = request.Zona,
                Tipo             = tipo,
                ValorNormalizado = Math.Round(valor, 2),
                UnidadePadrao    = _unidadesPadrao.GetValueOrDefault(tipo.ToUpper(), unidade),
                Timestamp        = ts,
                Valido           = valido,
                Observacao       = obs
            };
        }
    }
}