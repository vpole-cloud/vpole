FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.1 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.1 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY . .
RUN dotnet restore "src/tools/Pole.Connector/Pole.Connector.csproj"
RUN dotnet publish "src/tools/Pole.Connector/Pole.Connector.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Pole.Connector.dll"]
