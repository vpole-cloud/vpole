using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies.Backup;
using Hangfire.Mongo.UtcDateTime;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Platform.Hosting;
using Skrynia;

namespace Pole.Server.Extensions;

public static class PolePersistenceHosting
{
    public static IHostApplicationBuilder AddPolePersistence(this IHostApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection("Database");
        builder.Services.AddOptions<PoleDatabaseOptions>()
            .Bind(section)
            .Validate(options => Enum.IsDefined(options.Provider), "Unknown database provider.")
            .Validate(options => options.Provider != PoleDatabaseProvider.Skrynia ||
                !string.IsNullOrWhiteSpace(options.FilePath) && Path.IsPathFullyQualified(options.FilePath),
                "Database:FilePath must be an absolute path for Skrynia.")
            .ValidateOnStart();
        var provider = section.GetValue("Provider", PoleDatabaseProvider.MongoDB);
        switch (provider)
        {
            case PoleDatabaseProvider.MongoDB:
                builder.AddMongoDBClient("pole-database");
                builder.Services.AddHangfireWithMongo(builder.Configuration);
                break;
            case PoleDatabaseProvider.Skrynia:
                builder.Services.AddSingleton(services =>
                {
                    var options = services.GetRequiredService<IOptions<PoleDatabaseOptions>>().Value;
                    Directory.CreateDirectory(Path.GetDirectoryName(options.FilePath!)!);
                    return SkryniaClient.Open(options.FilePath!, new SkryniaOptions
                    {
                        LoggerFactory = services.GetRequiredService<ILoggerFactory>()
                    });
                });
                builder.Services.AddSingleton<IMongoClient>(services =>
                    services.GetRequiredService<SkryniaClient>().Client);
                builder.Services.AddHealthChecks().AddCheck<DatabaseReadinessCheck>("pole-database");
                builder.Services.AddHangfireWithMongo(builder.Configuration, options =>
                {
                    options.CheckQueuedJobsStrategy = CheckQueuedJobsStrategy.Poll;
                    options.SupportsCappedCollection = false;
                    options.MigrationOptions.BackupStrategy = new NoneMongoBackupStrategy();
                    options.UtcDateTimeStrategies = [new ServerStatusUtcDateTimeStrategy()];
                });
                break;
            default:
                throw new OptionsValidationException(nameof(PoleDatabaseOptions), typeof(PoleDatabaseOptions),
                    ["Unknown database provider."]);
        }
        return builder;
    }
}

public enum PoleDatabaseProvider { MongoDB, Skrynia }

public sealed class PoleDatabaseOptions
{
    public PoleDatabaseProvider Provider { get; set; } = PoleDatabaseProvider.MongoDB;
    public string? FilePath { get; set; }
}

internal sealed class DatabaseReadinessCheck(IMongoClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        await client.GetDatabase("pole-database").GetCollection<BsonDocument>("__migrations")
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
