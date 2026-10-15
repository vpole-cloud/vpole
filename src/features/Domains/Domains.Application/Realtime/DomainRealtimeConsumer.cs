using MassTransit;
using Pole.Features.Domains.Events;
using Pole.Features.Realtime.Contracts;

namespace Pole.Features.Domains.Application.Realtime;

public sealed class NotifyDomainMetadataChangedConsumer(PublishRealtimeChange publish)
    : IConsumer<DomainMetadataChangedEvent>
{
    public Task HandleAsync(DomainMetadataChangedEvent message, CancellationToken ct = default) =>
        publish(new("domains", message.EventCore.TenantId), ct);

    public Task Consume(ConsumeContext<DomainMetadataChangedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);
}
