ARG GATEWAY_DIR=Gateway_001

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG GATEWAY_DIR
WORKDIR /src
COPY protos/ protos/
COPY ${GATEWAY_DIR}/ gateway/
WORKDIR /src/gateway
RUN dotnet publish Gateway.csproj -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app/out .
ENTRYPOINT ["dotnet", "Gateway.dll"]
