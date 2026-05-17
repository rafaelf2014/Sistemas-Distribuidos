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

        public override Task<LeituraProcessada> Normalizar(LeituraBruta request, ServerCallContext context)
        {
            double valor = request.Valor;
            string obs   = "";

            // TODO: converter unidades (ex: F→C, mg/m³→ppm) consoante request.Unidade

            // Validar intervalo
            bool valido = true;
            if (_intervalos.TryGetValue(request.Tipo, out var intervalo))
            {
                if (valor < intervalo.Min || valor > intervalo.Max)
                {
                    valido = false;
                    obs    = $"Valor {valor} fora do intervalo [{intervalo.Min}, {intervalo.Max}] para {request.Tipo}";
                }
            }

            return Task.FromResult(new LeituraProcessada
            {
                GatewayId       = request.GatewayId,
                SensorId        = request.SensorId,
                Zona            = request.Zona,
                Tipo            = request.Tipo,
                ValorNormalizado = Math.Round(valor, 2),
                UnidadePadrao   = _unidadesPadrao.GetValueOrDefault(request.Tipo, request.Unidade),
                Timestamp       = request.Timestamp,
                Valido          = valido,
                Observacao      = obs
            });
        }
    }
}
