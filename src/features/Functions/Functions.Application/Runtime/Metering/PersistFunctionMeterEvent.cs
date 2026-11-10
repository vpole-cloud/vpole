using MongoDB.Driver;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Runtime.Metering;

public delegate Task PersistFunctionMeterEvent(
    FunctionMeterEvent meterEvent,
    CancellationToken cancellationToken = default);

public sealed class MongoPersistFunctionMeterEvent(
    IMongoCollection<FunctionMeterEvent> functionMeterEvents
)
{
    public async Task InvokeAsync(FunctionMeterEvent meterEvent, CancellationToken cancellationToken = default)
    {
        try
        {
            await functionMeterEvents.InsertOneAsync(meterEvent, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Deterministic ids make replay idempotent.
        }
    }
}
