using PreProcessamento.Services;

// Arranque do servico gRPC de pre-processamento (normalizacao de leituras).
var builder = WebApplication.CreateBuilder(args);

// Regista o suporte a gRPC.
builder.Services.AddGrpc();

var app = builder.Build();

// Liga o servico de normalizacao e uma pagina simples de verificacao.
app.MapGrpcService<NormalizacaoService>();
app.MapGet("/", () => "ONE HEALTH — Serviço de Pré-Processamento gRPC activo.");

app.Run();
