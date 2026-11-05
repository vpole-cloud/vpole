using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Application.Declarations;
using Pole.Features.Functions.Application;
using MongoDB.Driver;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Runtime.Metering;

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
