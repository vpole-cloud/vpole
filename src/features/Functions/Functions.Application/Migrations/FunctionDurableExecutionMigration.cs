using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Migrations;

public sealed class FunctionDurableExecutionMigration : IMigration
{
    public ObjectId Id => new("69d50000b213917a38c379f2");
    public string Name => "Function durable timer execution";
    public string Description => "Creates timer, invocation, metering, and usage indexes.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var timers = context.GetCollection<FunctionTimerState>("FunctionTimerStates");
        await timers.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<FunctionTimerState>(
                    Builders<FunctionTimerState>.IndexKeys
                        .Ascending(timer => timer.Enabled)
                        .Ascending(timer => timer.NextOccurrenceUtc)
                        .Ascending("Lease.ExpiresAtUtc"),
                    new CreateIndexOptions { Name = "IX_FunctionTimers_Due" }),
                new CreateIndexModel<FunctionTimerState>(
                    Builders<FunctionTimerState>.IndexKeys
                        .Ascending(timer => timer.TenantId)
                        .Ascending(timer => timer.FunctionAppId)
                        .Ascending(timer => timer.TriggerId),
                    new CreateIndexOptions
                    {
                        Name = "UX_FunctionTimers_Tenant_App_Trigger",
                        Unique = true
                    })
            ],
            cancellationToken);

        var invocations = context.GetCollection<FunctionInvocation>("FunctionInvocations");
        await invocations.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<FunctionInvocation>(
                    Builders<FunctionInvocation>.IndexKeys
                        .Ascending(invocation => invocation.State)
                        .Ascending(invocation => invocation.NextAttemptAtUtc)
                        .Ascending("Lease.ExpiresAtUtc")
                        .Ascending(invocation => invocation.CreatedAtUtc),
                    new CreateIndexOptions { Name = "IX_FunctionInvocations_Dispatch" }),
                new CreateIndexModel<FunctionInvocation>(
                    Builders<FunctionInvocation>.IndexKeys
                        .Ascending(invocation => invocation.TenantId)
                        .Ascending(invocation => invocation.TriggerId)
                        .Ascending(invocation => invocation.ScheduleVersion)
                        .Ascending(invocation => invocation.ScheduledForUtc),
                    new CreateIndexOptions
                    {
                        Name = "UX_FunctionInvocations_TimerOccurrence",
                        Unique = true
                    }),
                new CreateIndexModel<FunctionInvocation>(
                    Builders<FunctionInvocation>.IndexKeys
                        .Ascending(invocation => invocation.TenantId)
                        .Ascending(invocation => invocation.FunctionAppId)
                        .Ascending(invocation => invocation.RevisionId)
                        .Ascending(invocation => invocation.State),
                    new CreateIndexOptions { Name = "IX_FunctionInvocations_Revision_State" })
            ],
            cancellationToken);

        var meterEvents = context.GetCollection<FunctionMeterEvent>("FunctionMeterEvents");
        await meterEvents.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<FunctionMeterEvent>(
                    Builders<FunctionMeterEvent>.IndexKeys
                        .Ascending(meter => meter.AggregatedAtUtc)
                        .Ascending(meter => meter.OccurredAtUtcDateTime),
                    new CreateIndexOptions { Name = "IX_FunctionMeterEvents_Aggregation" }),
                new CreateIndexModel<FunctionMeterEvent>(
                    Builders<FunctionMeterEvent>.IndexKeys.Ascending(meter => meter.OccurredAtUtcDateTime),
                    new CreateIndexOptions
                    {
                        Name = "IX_FunctionMeterEvents_Retention",
                        ExpireAfter = TimeSpan.FromDays(30)
                    })
            ],
            cancellationToken);

        var usage = context.GetCollection<FunctionUsageBucket>("FunctionUsageBuckets");
        await usage.Indexes.CreateOneAsync(
            new CreateIndexModel<FunctionUsageBucket>(
                Builders<FunctionUsageBucket>.IndexKeys
                    .Ascending(bucket => bucket.TenantId)
                    .Descending(bucket => bucket.HourUtc),
                new CreateIndexOptions { Name = "IX_FunctionUsage_Tenant_Hour" }),
            cancellationToken: cancellationToken);
    }
}
