using Grpc.Core;
using PreProcessamento;

namespace PreProcessamento.Services
{
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
            double valor = request.Valor;
            string obs   = "";

            // Converter para unidade canónica antes de validar
            string unidade = request.Unidade.Trim();
            switch (request.Tipo.ToUpper())
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

            // Validar intervalo
            bool valido = true;
            if (_intervalos.TryGetValue(request.Tipo.ToUpper(), out var intervalo))
            {
                if (valor < intervalo.Min || valor > intervalo.Max)
                {
                    valido = false;
                    obs    = $"Valor {Math.Round(valor, 2)} fora do intervalo [{intervalo.Min}, {intervalo.Max}] para {request.Tipo}";
                }
            }

            return new LeituraProcessada
            {
                GatewayId        = request.GatewayId,
                SensorId         = request.SensorId,
                Zona             = request.Zona,
                Tipo             = request.Tipo,
                ValorNormalizado = Math.Round(valor, 2),
                UnidadePadrao    = _unidadesPadrao.GetValueOrDefault(request.Tipo, request.Unidade),
                Timestamp        = request.Timestamp,
                Valido           = valido,
                Observacao       = obs
            };
        }
    }
}
