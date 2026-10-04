using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Contracts;

public interface IResourceDeploymentDiagnosticsProvider
{
    Task<WorkloadDiagnostics> DiscoverAsync(ResourceDeploymentReconciliationSnapshot deployment, CancellationToken cancellationToken = default);
    Task<RuntimeLogOpenResult> OpenLogsAsync(ResourceDeploymentReconciliationSnapshot deployment, RuntimeLogSelection selection, CancellationToken cancellationToken = default);
}

public sealed record ResourceDeploymentDiagnosticsDto(
    string DeploymentId,
    string ResourceType,
    string ResourceId,
    string TargetId,
    long DeploymentRevision,
    long DesiredGeneration,
    DateTime CheckedAtUtc,
    WorkloadDiagnostics Diagnostics);
