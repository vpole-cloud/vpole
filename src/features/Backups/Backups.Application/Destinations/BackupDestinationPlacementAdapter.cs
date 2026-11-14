using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.Backups.Application.Destinations;

/// <summary>The placement owns the retained namespace reference for S3 credential projections.
/// Secrets reconciles those projections. The destination runs no pods of its own.</summary>
internal sealed class BackupDestinationPlacementAdapter : IProjectionOnlyPlacementAdapter
{
    public Task<ResourceDeploymentAdapterResult> ApplyAsync(
        ResourceDeploymentReconciliationContext context,
        CancellationToken ct) =>
        Task.FromResult<ResourceDeploymentAdapterResult>(ResourceDeploymentAdapterResult.Succeeded.Create());

    public Task<ResourceDeploymentAdapterResult> DeleteAsync(
        ResourceDeploymentReconciliationContext context,
        CancellationToken ct) =>
        Task.FromResult<ResourceDeploymentAdapterResult>(ResourceDeploymentAdapterResult.Succeeded.Create());
}
