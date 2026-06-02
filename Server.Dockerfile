FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY protos/ protos/
COPY Server/ Server/
WORKDIR /src/Server
RUN dotnet publish Server.csproj -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app/out .
EXPOSE 8080
EXPOSE 14000
EXPOSE 14001
ENTRYPOINT ["dotnet", "Server.dll"]
