using Microsoft.Extensions.Logging;
using Pole.Features.Functions.Application.Runtime.Metering;
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
    PersistFunctionMeterEvent persistMeterEvent,
    ILogger<FunctionMeterWriter> logger
) : IFunctionMeterWriter
{
    public async Task WriteAsync(FunctionMeterEvent meterEvent, CancellationToken cancellationToken = default)
    {
        await persistMeterEvent(meterEvent, cancellationToken);
    }

    public async Task TryWriteAsync(FunctionMeterEvent meterEvent, CancellationToken cancellationToken = default)
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
