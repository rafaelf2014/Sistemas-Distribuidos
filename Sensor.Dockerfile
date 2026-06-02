ARG SENSOR_DIR=Sensor_001

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG SENSOR_DIR
WORKDIR /src
COPY ${SENSOR_DIR}/ .
RUN dotnet publish Sensor.csproj -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app/out .
ENTRYPOINT ["dotnet", "Sensor.dll"]
