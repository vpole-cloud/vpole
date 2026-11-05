using MassTransit;
using Pole.Features.ResourceGroups.Events;
using Pole.Features.Secrets.Application.Bindings;

namespace Pole.Features.Secrets.Application.Consumers;

public sealed class RemoveSecretBindingsOnResourceUnregistered(RemoveConsumerSecretBindings removeBindings)
    : IConsumer<ResourceUnregisteredEvent>
{
    public Task Handle(
        ResourceUnregisteredEvent message,
        CancellationToken cancellationToken = default) =>
        removeBindings(
            message.EventCore.TenantId,
            message.ResourceGroupId,
            message.ResourceType,
            message.ResourceId,
            cancellationToken);

    public Task Consume(ConsumeContext<ResourceUnregisteredEvent> context) =>
        Handle(context.Message, context.CancellationToken);
}
