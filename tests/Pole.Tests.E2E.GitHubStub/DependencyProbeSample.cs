namespace Pole.Tests.E2E.GitHubStub;

/// <summary>Real application probes; responses contain fixed outcomes only, never credentials or exception bodies.</summary>
internal static class DependencyProbeSample
{
    internal const string Project = """
                                    <Project Sdk="Microsoft.NET.Sdk.Web">
                                      <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
                                      <ItemGroup>
                                        <PackageReference Include="Npgsql" Version="10.0.3" />
                                        <PackageReference Include="StackExchange.Redis" Version="3.3.1" />
                                      </ItemGroup>
                                    </Project>
                                    """;

    internal const string Dockerfile = """
                                       FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
                                       WORKDIR /src
                                       COPY src/ .
                                       RUN dotnet publish Pole.E2E.Sample.csproj -c Release -o /out /p:UseAppHost=false
                                       FROM mcr.microsoft.com/dotnet/aspnet:10.0
                                       WORKDIR /app
                                       COPY --from=build /out/ .
                                       ENV ASPNETCORE_HTTP_PORTS=8080
                                       ENTRYPOINT ["dotnet", "Pole.E2E.Sample.dll"]
                                       """;

    internal const string Program = """
                                    using Npgsql;
                                    using StackExchange.Redis;
                                    var builder = WebApplication.CreateBuilder(args);
                                    builder.Logging.ClearProviders();
                                    var app = builder.Build();
                                    app.MapGet("/health", () => "healthy");
                                    app.MapGet("/dependencies", async () => new { postgres = await Postgres(), redis = await Redis() });
                                    app.Run();

                                    static async Task<string> Postgres()
                                    {
                                        string? Read(string name) => Environment.GetEnvironmentVariable(name);
                                        if (new[] { "PGHOST", "PGPORT", "PGDATABASE", "PGUSER", "PGPASSWORD" }.Any(name => Read(name) is null)) return "MissingConfiguration";
                                        try
                                        {
                                            var options = new NpgsqlConnectionStringBuilder { Host = Read("PGHOST"), Port = int.Parse(Read("PGPORT")!), Database = Read("PGDATABASE"), Username = Read("PGUSER"), Password = Read("PGPASSWORD"), Timeout = 2, CommandTimeout = 2, Pooling = false };
                                            await using var connection = new NpgsqlConnection(options.ConnectionString);
                                            await connection.OpenAsync();
                                            await using var command = new NpgsqlCommand("SELECT 1", connection);
                                            return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1 ? "Succeeded" : "UnexpectedResult";
                                        }
                                        catch { return "ConnectionRejected"; }
                                    }

                                    static async Task<string> Redis()
                                    {
                                        string? Read(string name) => Environment.GetEnvironmentVariable(name);
                                        if (new[] { "REDISHOST", "REDISPORT", "REDISDB" }.Any(name => Read(name) is null)) return "MissingConfiguration";
                                        try
                                        {
                                            var options = new ConfigurationOptions { AbortOnConnectFail = true, ConnectTimeout = 2000, AsyncTimeout = 2000, ConnectRetry = 0 };
                                            options.EndPoints.Add(Read("REDISHOST")!, int.Parse(Read("REDISPORT")!));
                                            using var connection = await ConnectionMultiplexer.ConnectAsync(options);
                                            var database = connection.GetDatabase(int.Parse(Read("REDISDB")!));
                                            var key = "pole-e2e-connectivity";
                                            await database.StringSetAsync(key, "verified", TimeSpan.FromSeconds(20));
                                            var value = await database.StringGetAsync(key);
                                            await database.KeyDeleteAsync(key);
                                            return value == "verified" ? "Succeeded" : "UnexpectedResult";
                                        }
                                        catch { return "ConnectionRejected"; }
                                    }
                                    """;
}
