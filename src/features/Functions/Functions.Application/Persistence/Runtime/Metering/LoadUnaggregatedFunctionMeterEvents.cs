using MongoDB.Driver;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Persistence.Runtime.Metering;

public delegate Task<IReadOnlyList<FunctionMeterEvent>> LoadUnaggregatedFunctionMeterEvents(
    int take,
    CancellationToken cancellationToken = default);

public sealed class MongoLoadUnaggregatedFunctionMeterEvents(
    IMongoCollection<FunctionMeterEvent> functionMeterEvents)
{
    public async Task<IReadOnlyList<FunctionMeterEvent>> InvokeAsync(
        int take,
        CancellationToken cancellationToken = default) =>
        await functionMeterEvents.Find(FunctionMeterAggregationFilters.Unaggregated())
            .SortBy(meter => meter.OccurredAtUtcDateTime)
            .Limit(take)
            .ToListAsync(cancellationToken);
}

internal static class FunctionMeterAggregationFilters
{
    public static FilterDefinition<FunctionMeterEvent> Unaggregated() =>
        Builders<FunctionMeterEvent>.Filter.Eq(meter => meter.AggregatedAtUtc, null);

    public static FilterDefinition<FunctionMeterEvent> UnaggregatedById(string meterEventId) =>
        Builders<FunctionMeterEvent>.Filter.And(
            Builders<FunctionMeterEvent>.Filter.Eq(meter => meter.Id, meterEventId),
            Unaggregated());

    public static FilterDefinition<FunctionUsageBucket> ById(string bucketId) =>
        Builders<FunctionUsageBucket>.Filter.Eq(bucket => bucket.Id, bucketId);
}
