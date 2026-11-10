using MassTransit;
using Pole.Features.Realtime.Contracts;
using Pole.Features.Secrets.Events;

namespace Pole.Features.Secrets.Application.Realtime;

public sealed class NotifySecretChangedConsumer(
    PublishRealtimeChange publish
) : IConsumer<SecretChangedEvent>, IConsumer<SecretProjectionChangedEvent>
{
    public Task Consume(ConsumeContext<SecretChangedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<SecretProjectionChangedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task HandleAsync(SecretChangedEvent message, CancellationToken ct = default) => publish(
        new RealtimeChange("secrets", message.EventCore.TenantId, message.SecretId, Revision: message.Revision),
        ct);

    public Task HandleAsync(SecretProjectionChangedEvent message, CancellationToken ct = default) => publish(
        new RealtimeChange(
            "secrets",
            message.EventCore.TenantId,
            message.SecretId,
            message.ResourceGroupId,
            message.DesiredRevision,
            ObservationVersion: message.StorageRevision),
        ct);
}
