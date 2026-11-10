using MassTransit;
using Pole.Features.Realtime.Contracts;
using Pole.Features.SourceBuilds.Events;

namespace Pole.Features.SourceBuilds.Application.Realtime;

public sealed class NotifySourceBuildStateChangedConsumer(
    PublishRealtimeChange publish
) : IConsumer<SourceBuildJobStateChangedEvent>
{
    public Task Consume(ConsumeContext<SourceBuildJobStateChangedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task HandleAsync(SourceBuildJobStateChangedEvent message, CancellationToken cancellationToken = default) =>
        publish(
            new RealtimeChange("source-builds", message.EventCore.TenantId, message.WebServiceId),
            cancellationToken);
}
