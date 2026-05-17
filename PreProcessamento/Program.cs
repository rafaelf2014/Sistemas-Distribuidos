using PreProcessamento.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddGrpc();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.MapGrpcService<NormalizacaoService>();
app.MapGet("/", () => "ONE HEALTH — Serviço de Pré-Processamento gRPC activo.");

app.Run();
