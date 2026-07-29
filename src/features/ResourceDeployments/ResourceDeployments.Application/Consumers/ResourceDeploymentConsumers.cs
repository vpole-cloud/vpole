using MassTransit;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceDeployments.Events;

namespace Pole.Features.ResourceDeployments.Application.Consumers;

public sealed class ReconcileResourceDeploymentConsumer(
    ResourceDeploymentReconciler reconciler)
    : IConsumer<ResourceDeploymentReconciliationRequestedEvent>
{
    public Task Consume(
        ConsumeContext<ResourceDeploymentReconciliationRequestedEvent> context) =>
        reconciler.HandleAsync(
            context.Message.EventCore.TenantId,
            context.Message.ResourceDeploymentId,
            context.Message.RuntimeCellId,
            context.Message.Generation,
            ResourceDeploymentOperation.Apply,
            context.CancellationToken);
}

public sealed class DeleteResourceDeploymentConsumer(
    ResourceDeploymentReconciler reconciler)
    : IConsumer<ResourceDeploymentDeletionRequestedEvent>
{
    public Task Consume(
        ConsumeContext<ResourceDeploymentDeletionRequestedEvent> context) =>
        reconciler.HandleAsync(
            context.Message.EventCore.TenantId,
            context.Message.ResourceDeploymentId,
            context.Message.RuntimeCellId,
            context.Message.Generation,
            ResourceDeploymentOperation.Delete,
            context.CancellationToken);
}

public sealed class ReconcileResourceDeploymentConsumerDefinition
    : ConsumerDefinition<ReconcileResourceDeploymentConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ReconcileResourceDeploymentConsumer> consumerConfigurator,
        IRegistrationContext context) =>
        endpointConfigurator.UseMessageRetry(retry =>
            retry.Intervals(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)));
}

public sealed class DeleteResourceDeploymentConsumerDefinition
    : ConsumerDefinition<DeleteResourceDeploymentConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<DeleteResourceDeploymentConsumer> consumerConfigurator,
        IRegistrationContext context) =>
        endpointConfigurator.UseMessageRetry(retry =>
            retry.Intervals(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)));
}
