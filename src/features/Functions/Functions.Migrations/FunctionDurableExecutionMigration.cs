using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.Functions.Migrations;

public sealed class FunctionDurableExecutionMigration : IMigration
{
    public ObjectId Id => new("69d50000b213917a38c379f2");
    public string Name => "Function durable timer execution";
    public string Description => "Creates timer, invocation, metering, and usage indexes.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var timers = context.GetCollection<BsonDocument>("FunctionTimerStates");
        await timers.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("Enabled")
                        .Ascending("NextOccurrenceUtc")
                        .Ascending("Lease.ExpiresAtUtc"),
                    new CreateIndexOptions { Name = "IX_FunctionTimers_Due" }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("TenantId")
                        .Ascending("FunctionAppId")
                        .Ascending("TriggerId"),
                    new CreateIndexOptions
                    {
                        Name = "UX_FunctionTimers_Tenant_App_Trigger",
                        Unique = true,
                    }),
            ],
            cancellationToken);

        var invocations = context.GetCollection<BsonDocument>("FunctionInvocations");
        await invocations.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("State")
                        .Ascending("NextAttemptAtUtc")
                        .Ascending("Lease.ExpiresAtUtc")
                        .Ascending("CreatedAtUtc"),
                    new CreateIndexOptions { Name = "IX_FunctionInvocations_Dispatch" }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("TenantId")
                        .Ascending("TriggerId")
                        .Ascending("ScheduleVersion")
                        .Ascending("ScheduledForUtc"),
                    new CreateIndexOptions
                    {
                        Name = "UX_FunctionInvocations_TimerOccurrence",
                        Unique = true,
                    }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("TenantId")
                        .Ascending("FunctionAppId")
                        .Ascending("RevisionId")
                        .Ascending("State"),
                    new CreateIndexOptions { Name = "IX_FunctionInvocations_Revision_State" }),
            ],
            cancellationToken);

        var meterEvents = context.GetCollection<BsonDocument>("FunctionMeterEvents");
        await meterEvents.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("AggregatedAtUtc").Ascending("OccurredAtUtcDateTime"),
                    new CreateIndexOptions { Name = "IX_FunctionMeterEvents_Aggregation" }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("OccurredAtUtcDateTime"),
                    new CreateIndexOptions
                    {
                        Name = "IX_FunctionMeterEvents_Retention",
                        ExpireAfter = TimeSpan.FromDays(30),
                    }),
            ],
            cancellationToken);

        var usage = context.GetCollection<BsonDocument>("FunctionUsageBuckets");
        await usage.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Descending("HourUtc"),
                new CreateIndexOptions { Name = "IX_FunctionUsage_Tenant_Hour" }),
            cancellationToken: cancellationToken);
    }
}
