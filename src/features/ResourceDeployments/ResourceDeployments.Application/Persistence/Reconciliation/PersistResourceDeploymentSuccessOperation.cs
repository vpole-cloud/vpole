using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Reconciliation;

public delegate Task PersistResourceDeploymentSuccess(
    ResourceDeployment claimed,
    long generation,
    ResourceDeploymentOperation operation,
    IReadOnlyCollection<ResourceDeploymentEndpoint> endpoints,
    CancellationToken cancellationToken = default);

internal sealed class PersistResourceDeploymentSuccessOperation(
    ResourceDeploymentCompletionPersistence completionPersistence,
    TimeProvider timeProvider)
{
    public Task InvokeAsync(
        ResourceDeployment claimed,
        long generation,
        ResourceDeploymentOperation operation,
        IReadOnlyCollection<ResourceDeploymentEndpoint> endpoints,
        CancellationToken cancellationToken = default)
    {
        var claim = ResourceDeploymentCompletionPersistence.GetActiveClaim(claimed);
        return completionPersistence.PersistAsync(
            claimed,
            current => current.Complete(
                generation,
                operation,
                claim.ClaimId,
                endpoints,
                timeProvider.GetUtcNow().UtcDateTime),
            cancellationToken);
    }
}
