// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.ResourceGroups.Application.Jobs;
using Pole.Features.ResourceGroups.Application.Kubernetes;
using Pole.Features.ResourceGroups.Application.RuntimeCells;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Events;

namespace Pole.Features.ResourceGroups.Application.Consumers;

public sealed class ReconcileRuntimeCellOnReconciliationRequestedConsumer(
    RuntimeCellLifecyclePersistence persistence,
    EnsureRuntimeCellNamespace ensureRuntimeCellNamespace
) : IConsumer<RuntimeCellReconciliationRequestedEvent>
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
        if (runtimeCell is null || runtimeCell.DesiredGeneration != message.Generation ||
            runtimeCell.State is not ProvisioningRuntimeCellState provisioning ||
            provisioning.Generation != message.Generation)
            return;

        var namespaceResult = await ensureRuntimeCellNamespace(runtimeCell, cancellationToken);
        await persistence.PersistReconciliationAsync(
            runtimeCell,
            message.Generation,
            namespaceResult,
            cancellationToken);
    }
}

public sealed class DeleteRuntimeCellOnDeletionRequestedConsumer(
    RuntimeCellDeletionProgress progress,
    ScheduleRuntimeCellDeletionAt schedule
) : IConsumer<RuntimeCellDeletionRequestedEvent>
{
    public Task Consume(ConsumeContext<RuntimeCellDeletionRequestedEvent> context)
    {
        if (context.Message.NotBeforeUtc is { } due)
        {
            schedule(context.Message, due);
            return Task.CompletedTask;
        }

        return progress.HandleAsync(context.Message, context.CancellationToken);
    }
}

public sealed class ReconcileRuntimeCellConsumerDefinition
    : ConsumerDefinition<ReconcileRuntimeCellOnReconciliationRequestedConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ReconcileRuntimeCellOnReconciliationRequestedConsumer> consumerConfigurator,
        IRegistrationContext context) => endpointConfigurator.UseMessageRetry(retry =>
        retry.Intervals(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)));
}

public sealed class DeleteRuntimeCellConsumerDefinition
    : ConsumerDefinition<DeleteRuntimeCellOnDeletionRequestedConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<DeleteRuntimeCellOnDeletionRequestedConsumer> consumerConfigurator,
        IRegistrationContext context) => endpointConfigurator.UseMessageRetry(retry =>
        retry.Intervals(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)));
}
