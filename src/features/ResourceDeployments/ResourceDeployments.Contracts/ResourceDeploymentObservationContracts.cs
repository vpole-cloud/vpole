using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Contracts;

/// <summary>Providers choose native objects; the platform supplies only persisted owned placement.</summary>
public interface IResourceDeploymentObserver
{
    Task<WorkloadObservation> ObserveAsync(
        ResourceDeploymentReconciliationSnapshot deployment,
        CancellationToken cancellationToken = default);
}

public sealed record ResourceDeploymentObservationContext(
    ResourceDeploymentReconciliationSnapshot Deployment,
    long DeploymentRevision,
    long TargetConnectionVersion);

public enum ResourceDeploymentObservationChangeKind
{
    SourceReady,
    Invalidated,
    BindingChanged,
    Unavailable
}

/// <summary>Only a hint and an opaque binding; native models stay inside their provider.</summary>
public sealed record ResourceDeploymentObservationChange(
    ResourceDeploymentObservationChangeKind Kind,
    string? BindingId = null);

public interface IResourceDeploymentObservationChangeSource
{
    IAsyncEnumerable<ResourceDeploymentObservationChange> WatchAsync(
        ResourceDeploymentObservationContext context,
        CancellationToken cancellationToken = default);
}

public sealed record ResourceDeploymentBoundObservation(
    string? BindingId,
    WorkloadObservation Observation)
{
    public WorkloadDiagnostics? Diagnostics { get; init; }
}

/// <summary>Revalidates the source's exact native controller before and after sampling.</summary>
public interface IResourceDeploymentBoundObserver
{
    Task<ResourceDeploymentBoundObservation> ObserveBoundAsync(
        ResourceDeploymentObservationContext context,
        string bindingId,
        CancellationToken cancellationToken = default);
}

/// <summary>One bounded read budget shared by HTTP sampling, snapshots and initial LISTs.</summary>
public interface IResourceDeploymentObservationReadBudget
{
    Task<T> ReadAsync<T>(
        Func<CancellationToken, Task<T>> read,
        CancellationToken cancellationToken = default);
}

public sealed record ResourceDeploymentObserverDescriptor(
    ResourceTypeId ResourceType,
    Type ObserverType)
{
    public Type? DiagnosticsType { get; init; }
    public Type? ChangeSourceType { get; init; }
    public Type? BoundObserverType { get; init; }
    public static ResourceDeploymentObserverDescriptor Create<TObserver>(ResourceTypeId resourceType)
        where TObserver : class, IResourceDeploymentObserver => new(resourceType, typeof(TObserver))
        {
            DiagnosticsType = typeof(IResourceDeploymentDiagnosticsProvider).IsAssignableFrom(typeof(TObserver)) ? typeof(TObserver) : null
        };

    public static ResourceDeploymentObserverDescriptor Create<TObserver, TSource>(ResourceTypeId resourceType)
        where TObserver : class, IResourceDeploymentObserver, IResourceDeploymentBoundObserver
        where TSource : class, IResourceDeploymentObservationChangeSource => Create<TObserver>(resourceType) with
        {
            ChangeSourceType = typeof(TSource),
            BoundObserverType = typeof(TObserver)
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
    WorkloadObservation Observation)
{
    public long? ObservationVersion { get; init; }
}
