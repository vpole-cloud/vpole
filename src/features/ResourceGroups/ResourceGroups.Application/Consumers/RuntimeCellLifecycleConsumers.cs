using MassTransit;
using Pole.Features.ResourceGroups.Application.Kubernetes;
using Pole.Features.ResourceGroups.Application.Persistence;
using Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups;
using Pole.Features.ResourceGroups.Application.Persistence.RuntimeCells;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Events;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Consumers;

public sealed class ReconcileRuntimeCellOnReconciliationRequestedConsumer(
    RuntimeCellLifecyclePersistence persistence,
    EnsureRuntimeCellNamespace ensureRuntimeCellNamespace)
    : IConsumer<RuntimeCellReconciliationRequestedEvent>
{
    public Task Consume(ConsumeContext<RuntimeCellReconciliationRequestedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public async Task HandleAsync(
        RuntimeCellReconciliationRequestedEvent message,
        CancellationToken cancellationToken = default)
    {
        var runtimeCell = await persistence.LoadAsync(
            message.EventCore.TenantId,
            message.RuntimeCellId,
            cancellationToken);
        if (runtimeCell is null ||
            runtimeCell.DesiredGeneration != message.Generation ||
            runtimeCell.State is not ProvisioningRuntimeCellState provisioning ||
            provisioning.Generation != message.Generation)
        {
            return;
        }

        var namespaceResult = await ensureRuntimeCellNamespace(
            runtimeCell,
            cancellationToken);
        await persistence.PersistReconciliationAsync(
            runtimeCell,
            message.Generation,
            namespaceResult,
            cancellationToken);
    }
}

public sealed class DeleteRuntimeCellOnDeletionRequestedConsumer(
    RuntimeCellLifecyclePersistence persistence,
    DeleteRuntimeCellNamespace deleteRuntimeCellNamespace,
    FinalizeResourceGroupDeletion finalizeResourceGroupDeletion)
    : IConsumer<RuntimeCellDeletionRequestedEvent>
{
    public Task Consume(ConsumeContext<RuntimeCellDeletionRequestedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public async Task HandleAsync(
        RuntimeCellDeletionRequestedEvent message,
        CancellationToken cancellationToken = default)
    {
        var runtimeCell = await persistence.LoadAsync(
            message.EventCore.TenantId,
            message.RuntimeCellId,
            cancellationToken);
        if (runtimeCell is null ||
            runtimeCell.DesiredGeneration != message.Generation ||
            runtimeCell.State is not DeletingRuntimeCellState deleting ||
            deleting.Generation != message.Generation)
        {
            return;
        }

        var namespaceResult = await deleteRuntimeCellNamespace(
            runtimeCell,
            cancellationToken);
        var finalizationAllowed = await persistence.PersistDeletionAsync(
            runtimeCell,
            message.Generation,
            namespaceResult,
            cancellationToken);
        if (finalizationAllowed)
        {
            await finalizeResourceGroupDeletion(
                runtimeCell.TenantId,
                runtimeCell.ResourceGroupId,
                runtimeCell.IsolationScopeId,
                cancellationToken);
        }
    }
}

public sealed class ReconcileRuntimeCellConsumerDefinition
    : ConsumerDefinition<ReconcileRuntimeCellOnReconciliationRequestedConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ReconcileRuntimeCellOnReconciliationRequestedConsumer> consumerConfigurator,
        IRegistrationContext context) =>
        endpointConfigurator.UseMessageRetry(retry =>
            retry.Intervals(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)));
}

public sealed class DeleteRuntimeCellConsumerDefinition
    : ConsumerDefinition<DeleteRuntimeCellOnDeletionRequestedConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<DeleteRuntimeCellOnDeletionRequestedConsumer> consumerConfigurator,
        IRegistrationContext context) =>
        endpointConfigurator.UseMessageRetry(retry =>
            retry.Intervals(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)));
}
