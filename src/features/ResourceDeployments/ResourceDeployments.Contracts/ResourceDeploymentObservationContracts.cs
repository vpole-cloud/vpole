using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Contracts;

/// <summary>Providers choose native objects; the platform supplies only persisted owned placement.</summary>
public interface IResourceDeploymentObserver
{
    Task<WorkloadObservation> ObserveAsync(
        ResourceDeploymentReconciliationSnapshot deployment,
        CancellationToken cancellationToken = default);
}

public sealed record ResourceDeploymentObserverDescriptor(
    ResourceTypeId ResourceType,
    Type ObserverType)
{
    public Type? DiagnosticsType { get; init; }
    public static ResourceDeploymentObserverDescriptor Create<TObserver>(ResourceTypeId resourceType)
        where TObserver : class, IResourceDeploymentObserver => new(resourceType, typeof(TObserver))
        {
            DiagnosticsType = typeof(IResourceDeploymentDiagnosticsProvider).IsAssignableFrom(typeof(TObserver)) ? typeof(TObserver) : null
        };
}

public sealed record ResourceDeploymentOperationDto(
    ResourceDeploymentDto Deployment,
    string? ResourceDisplayName,
    string Phase,
    int AttemptCount,
    DateTime SinceUtc,
    bool Retryable,
    DateTime? RetryNotBeforeUtc)
{
    public long OperationGeneration { get; init; } = 1;
    public string? TargetDisplayName { get; init; }
}

public sealed record ResourceDeploymentObservationDto(
    string DeploymentId,
    long DeploymentRevision,
    long DesiredGeneration,
    DateTime CheckedAtUtc,
    WorkloadObservation Observation);
