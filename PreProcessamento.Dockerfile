FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY protos/ protos/
COPY PreProcessamento/ PreProcessamento/
WORKDIR /src/PreProcessamento
RUN dotnet publish PreProcessamento.csproj -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app/out .
EXPOSE 50051
ENTRYPOINT ["dotnet", "PreProcessamento.dll"]
