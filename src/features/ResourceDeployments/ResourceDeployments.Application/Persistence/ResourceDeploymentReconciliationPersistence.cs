using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Persistence;

public delegate Task<ResourceDeployment?> ClaimResourceDeployment(
    string tenantId,
    string deploymentId,
    string runtimeCellId,
    long generation,
    ResourceDeploymentOperation operation,
    CancellationToken cancellationToken = default);

public delegate Task<Result> PersistResourceDeploymentSuccess(
    ResourceDeployment claimed,
    long generation,
    ResourceDeploymentOperation operation,
    IReadOnlyCollection<ResourceDeploymentEndpoint> endpoints,
    CancellationToken cancellationToken = default);

public delegate Task<Result> PersistResourceDeploymentFailure(
    ResourceDeployment claimed,
    long generation,
    ResourceDeploymentOperation operation,
    string failureCode,
    string failureMessage,
    bool retryable,
    CancellationToken cancellationToken = default);

internal sealed class ResourceDeploymentReconciliationPersistence(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    RunMongoTransaction runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites,
    ResourceDeploymentSettings settings,
    ResourceDeploymentRetryDelayPolicy retryDelayPolicy,
    TimeProvider timeProvider)
{
    public async Task<ResourceDeployment?> ClaimAsync(
        string tenantId,
        string deploymentId,
        string runtimeCellId,
        long generation,
        ResourceDeploymentOperation operation,
        CancellationToken cancellationToken = default)
    {
        var current = await resourceDeployments
            .Find(deployment =>
                deployment.Id == deploymentId &&
                deployment.TenantId == tenantId &&
                deployment.RuntimeCellId == runtimeCellId &&
                deployment.DesiredGeneration == generation &&
                deployment.DesiredOperation == operation)
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null ||
            current.State is not PendingResourceDeploymentState pending ||
            pending.Generation != generation ||
            pending.Operation != operation)
        {
            return null;
        }

        var startedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var claimed = current.Start(
            generation,
            operation,
            ObjectId.GenerateNewId().ToString(),
            startedAtUtc,
            startedAtUtc.AddSeconds(settings.ClaimLeaseSeconds));
        var replacement = await resourceDeployments.ReplaceOneAsync(
            Builders<ResourceDeployment>.Filter.And(
                Builders<ResourceDeployment>.Filter.Eq(
                    deployment => deployment.Id,
                    current.Id),
                Builders<ResourceDeployment>.Filter.Eq(
                    deployment => deployment.TenantId,
                    current.TenantId),
                Builders<ResourceDeployment>.Filter.Eq(
                    deployment => deployment.Revision,
                    current.Revision),
                Builders<ResourceDeployment>.Filter
                    .OfType<ResourceDeploymentState, PendingResourceDeploymentState>(
                        deployment => deployment.State)),
            claimed,
            cancellationToken: cancellationToken);
        return replacement.ModifiedCount == 1
            ? claimed
            : null;
    }

    public Task<Result> PersistSuccessAsync(
        ResourceDeployment claimed,
        long generation,
        ResourceDeploymentOperation operation,
        IReadOnlyCollection<ResourceDeploymentEndpoint> endpoints,
        CancellationToken cancellationToken = default) =>
        PersistCompletionAsync(
            claimed,
            current => current.Complete(
                generation,
                operation,
                GetClaim(claimed).ClaimId,
                endpoints,
                timeProvider.GetUtcNow().UtcDateTime),
            cancellationToken);

    public Task<Result> PersistFailureAsync(
        ResourceDeployment claimed,
        long generation,
        ResourceDeploymentOperation operation,
        string failureCode,
        string failureMessage,
        bool retryable,
        CancellationToken cancellationToken = default)
    {
        var claim = GetClaim(claimed);
        var failedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        return PersistCompletionAsync(
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

    private Task<Result> PersistCompletionAsync(
        ResourceDeployment claimed,
        Func<ResourceDeployment, ResourceDeployment> transition,
        CancellationToken cancellationToken) =>
        runMongoTransaction(async (session, ct) =>
        {
            var current = await resourceDeployments
                .Find(session, deployment =>
                    deployment.Id == claimed.Id &&
                    deployment.TenantId == claimed.TenantId)
                .FirstOrDefaultAsync(ct);
            if (current is null ||
                current.State is not ReconcilingResourceDeploymentState activeClaim ||
                claimed.State is not ReconcilingResourceDeploymentState expectedClaim ||
                !string.Equals(
                    activeClaim.ClaimId,
                    expectedClaim.ClaimId,
                    StringComparison.Ordinal))
            {
                return Result.Success();
            }

            var completed = transition(current);
            var replacement = await resourceDeployments.ReplaceOneAsync(
                session,
                Builders<ResourceDeployment>.Filter.And(
                    Builders<ResourceDeployment>.Filter.Eq(
                        deployment => deployment.Id,
                        current.Id),
                    Builders<ResourceDeployment>.Filter.Eq(
                        deployment => deployment.TenantId,
                        current.TenantId),
                    Builders<ResourceDeployment>.Filter.Eq(
                        deployment => deployment.Revision,
                        current.Revision),
                    Builders<ResourceDeployment>.Filter
                        .OfType<ResourceDeploymentState, ReconcilingResourceDeploymentState>(
                            deployment => deployment.State),
                    Builders<ResourceDeployment>.Filter.Eq(
                        deployment =>
                            ((ReconcilingResourceDeploymentState)deployment.State).ClaimId,
                        activeClaim.ClaimId)),
                completed,
                cancellationToken: ct);
            if (replacement.ModifiedCount != 1)
                return Result.Success();

            await storeOutboxWrites(
                session,
                [ToCompletionWrite(completed)],
                ct);
            return Result.Success();
        }, cancellationToken);

    private static ReconcilingResourceDeploymentState GetClaim(
        ResourceDeployment claimed) =>
        claimed.State as ReconcilingResourceDeploymentState
        ?? throw new InvalidOperationException(
            "The resource deployment has no active claim.");

    private static OutboxWrite ToCompletionWrite(
        ResourceDeployment completed) => completed.State switch
        {
            PendingResourceDeploymentState => completed.ToCurrentRequestWrite(),
            AppliedResourceDeploymentState applied =>
                OutboxWrite.Create(completed.ToAppliedEvent(applied.Generation)),
            FailedResourceDeploymentState failed =>
                OutboxWrite.Create(completed.ToFailedEvent(failed)),
            DeletedResourceDeploymentState deleted =>
                OutboxWrite.Create(completed.ToDeletedEvent(deleted.Generation)),
            _ => throw new InvalidOperationException(
                $"Unsupported resource deployment completion state '{completed.State.GetType().Name}'.")
        };
}
