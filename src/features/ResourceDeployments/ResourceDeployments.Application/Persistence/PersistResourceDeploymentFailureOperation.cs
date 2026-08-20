using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Persistence;

public delegate Task<Result> PersistResourceDeploymentFailure(
    ResourceDeployment claimed,
    long generation,
    ResourceDeploymentOperation operation,
    string failureCode,
    string failureMessage,
    bool retryable,
    CancellationToken cancellationToken = default);

internal sealed class PersistResourceDeploymentFailureOperation(
    ResourceDeploymentCompletionPersistence completionPersistence,
    ResourceDeploymentRetryDelayPolicy retryDelayPolicy,
    TimeProvider timeProvider)
{
    public Task<Result> InvokeAsync(
        ResourceDeployment claimed,
        long generation,
        ResourceDeploymentOperation operation,
        string failureCode,
        string failureMessage,
        bool retryable,
        CancellationToken cancellationToken = default)
    {
        var claim = ResourceDeploymentCompletionPersistence.GetActiveClaim(claimed);
        var failedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        return completionPersistence.PersistAsync(
            claimed,
            current => current.Fail(
                generation,
                operation,
                claim.ClaimId,
                failureCode,
                failureMessage,
                retryable,
                failedAtUtc,
                failedAtUtc.Add(retryDelayPolicy.Calculate(claim.AttemptCount))),
            cancellationToken);
    }
}
