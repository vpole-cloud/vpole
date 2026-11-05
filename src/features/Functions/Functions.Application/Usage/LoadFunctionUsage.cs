using Pole.Features.Functions.Application.Revisions;
using Pole.Features.Functions.Application.Invocations;
using Pole.Features.Functions.Application.FunctionApps;
using MongoDB.Driver;
using Pole.Features.Functions.Application;
using Pole.Features.Functions.Application.Declarations;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Usage;

public sealed record FunctionUsageSnapshot(
    DateTimeOffset? AggregatedThroughUtc,
    IReadOnlyList<FunctionUsageBucket> Buckets);

public delegate Task<FunctionUsageSnapshot> LoadFunctionUsage(
    FunctionApp functionApp,
    DateTimeOffset? fromUtc,
    DateTimeOffset? toUtc,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadFunctionUsage(
    IMongoCollection<FunctionUsageBucket> functionUsageBuckets,
    IMongoCollection<FunctionMeterEvent> functionMeterEvents)
{
    public async Task<FunctionUsageSnapshot> InvokeAsync(
        FunctionApp functionApp,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        CancellationToken cancellationToken = default)
    {
        var buckets = await functionUsageBuckets
            .Find(FunctionUsageFilters.ForRange(functionApp, fromUtc, toUtc))
            .SortBy(bucket => bucket.HourUtc)
            .ToListAsync(cancellationToken);
        var aggregatedThrough = await functionMeterEvents
            .Find(FunctionUsageFilters.AggregatedFor(functionApp))
            .SortByDescending(meter => meter.AggregatedAtUtc)
            .Project(meter => meter.AggregatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        return new FunctionUsageSnapshot(aggregatedThrough, buckets);
    }
}
