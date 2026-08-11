using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.ResourceDeployments.Events;

[EntityName("resource-deployment-reconciliation-requested")]
public sealed record ResourceDeploymentReconciliationRequestedEvent(
    string ResourceDeploymentId,
    string RuntimeCellId,
    long Generation,
    long ResourceRevision = 1) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

[EntityName("resource-deployment-deletion-requested")]
public sealed record ResourceDeploymentDeletionRequestedEvent(
    string ResourceDeploymentId,
    string RuntimeCellId,
    long Generation,
    long ResourceRevision = 1) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

[EntityName("resource-deployment-applied")]
public sealed record ResourceDeploymentAppliedEvent(
    string ResourceDeploymentId,
    string ResourceType,
    string ResourceId,
    string RuntimeCellId,
    long Generation,
    long ResourceRevision = 1) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

[EntityName("resource-deployment-failed")]
public sealed record ResourceDeploymentFailedEvent(
    string ResourceDeploymentId,
    string ResourceType,
    string ResourceId,
    string RuntimeCellId,
    long Generation,
    string Operation,
    string FailureCode,
    bool Retryable,
    long ResourceRevision = 1) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

[EntityName("resource-deployment-deleted")]
public sealed record ResourceDeploymentDeletedEvent(
    string ResourceDeploymentId,
    string ResourceType,
    string ResourceId,
    string RuntimeCellId,
    long Generation,
    long ResourceRevision = 1) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

public static class ResourceDeploymentPendingKey
{
    public static string Apply(string deploymentId, long generation) =>
        Create(deploymentId, generation, "apply");

    public static string Delete(string deploymentId, long generation) =>
        Create(deploymentId, generation, "delete");

    private static string Create(
        string deploymentId,
        long generation,
        string operation)
    {
        if (string.IsNullOrWhiteSpace(deploymentId))
            throw new ArgumentException("Deployment ID is required.", nameof(deploymentId));
        if (generation < 1)
            throw new ArgumentOutOfRangeException(nameof(generation), "Generation must be positive.");

        return $"resource-deployment:{deploymentId.Trim()}:{generation}:{operation}";
    }
}
