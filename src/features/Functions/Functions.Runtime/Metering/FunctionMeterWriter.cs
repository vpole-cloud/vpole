using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Runtime.Metering;

public interface IFunctionMeterWriter
{
    // Required writes are used before admission becomes externally observable.
    Task WriteAsync(FunctionMeterEvent meterEvent, CancellationToken cancellationToken = default);

    // Post-start telemetry must never replace the result of an already executing user function.
    Task TryWriteAsync(FunctionMeterEvent meterEvent, CancellationToken cancellationToken = default);
}

public sealed class FunctionMeterWriter(
    IFunctionAppDbContext dbContext,
    ILogger<FunctionMeterWriter> logger) : IFunctionMeterWriter
{
    public async Task WriteAsync(
        FunctionMeterEvent meterEvent,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.FunctionMeterEvents.InsertOneAsync(
                meterEvent,
                cancellationToken: cancellationToken);
        }
        catch (MongoWriteException exception) when (
            exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Deterministic ids make replay idempotent.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    public async Task TryWriteAsync(
        FunctionMeterEvent meterEvent,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await WriteAsync(meterEvent, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[function-meter-write-failed] Failed to persist post-start meter event {MeterEventId}",
                meterEvent.Id);
        }
    }
}
