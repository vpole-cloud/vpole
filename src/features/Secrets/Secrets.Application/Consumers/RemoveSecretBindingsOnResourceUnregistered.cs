using MassTransit;
using Pole.Features.ResourceGroups.Events;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.Values;

namespace Pole.Features.Secrets.Application;

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
