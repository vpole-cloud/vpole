FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080;8081;8082
EXPOSE 8080
EXPOSE 8081
EXPOSE 8082

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["api/Pole.Server/Pole.Server.csproj", "api/Pole.Server/"]
RUN dotnet restore "api/Pole.Server/Pole.Server.csproj"
COPY . .
WORKDIR "/src/api/Pole.Server"
RUN dotnet build "./Pole.Server.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Pole.Server.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Pole.Server.dll"]
