using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using k8s.Autorest;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Reconciliation;

public sealed class ResourceDeploymentReconciler(
    IResourceDeploymentDbContext dbContext,
    IResourcePlacementLookup placementLookup,
    ResourceDeploymentAdapterCatalog adapterCatalog,
    IServiceProvider serviceProvider,
    ResourceDeploymentSettings settings,
    ResourceDeploymentRetryDelayPolicy retryDelayPolicy,
    ResourceDeploymentMetrics metrics,
    TimeProvider timeProvider,
    ILogger<ResourceDeploymentReconciler> logger)
{
    public async Task HandleAsync(
        string tenantId,
        string deploymentId,
        string runtimeCellId,
        long generation,
        ResourceDeploymentOperation operation,
        CancellationToken cancellationToken = default)
    {
        var claimed = await TryClaimAsync(
            tenantId,
            deploymentId,
            runtimeCellId,
            generation,
            operation,
            cancellationToken);
        if (claimed is null)
            return;
        metrics.RecordAttempt(claimed.ResourceType, operation);

        var runtimeCell = await placementLookup.FindAsync(
            claimed.TenantId,
            claimed.RuntimeCellId,
            cancellationToken);
        if (runtimeCell is null ||
            !string.Equals(runtimeCell.DeploymentTargetId,
                claimed.ResolvedPlacement.DeploymentTargetId,
                StringComparison.Ordinal) ||
            !string.Equals(runtimeCell.KubernetesNamespace,
                claimed.ResolvedPlacement.KubernetesNamespace,
                StringComparison.Ordinal))
        {
            await PersistFailureAsync(
                claimed,
                generation,
                operation,
                "PlacementOwnershipMismatch",
                "The persisted runtime-cell placement does not match the resource deployment.",
                retryable: false,
                cancellationToken);
            return;
        }

        if (operation == ResourceDeploymentOperation.Apply &&
            runtimeCell.State != ResourcePlacementState.Ready)
        {
            await PersistFailureAsync(
                claimed,
                generation,
                operation,
                "RuntimeCellNotReady",
                "The resource deployment runtime cell is not ready.",
                retryable: true,
                cancellationToken);
            return;
        }

        var descriptor = adapterCatalog.GetRequired(claimed.ResourceType);
        if (descriptor.PlacementCapability != claimed.PlacementCapability)
        {
            await PersistFailureAsync(
                claimed,
                generation,
                operation,
                "PlacementCapabilityMismatch",
                "The persisted placement capability does not match the registered resource adapter.",
                retryable: false,
                cancellationToken);
            return;
        }

        var adapter = (IResourceDeploymentAdapter)serviceProvider
            .GetRequiredService(descriptor.AdapterType);
        var context = new ResourceDeploymentReconciliationContext(
            claimed.ToReconciliationSnapshot());
        var adapterStartedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        ResourceDeploymentAdapterResult result;
        try
        {
            result = operation == ResourceDeploymentOperation.Apply
                ? await adapter.ApplyAsync(context, cancellationToken)
                : await adapter.DeleteAsync(context, cancellationToken);
        }
        catch (DeploymentTargetUnavailableException exception)
        {
            await PersistFailureAsync(
                claimed,
                generation,
                operation,
                $"DeploymentTarget{exception.Reason}",
                "The deployment target Kubernetes client is unavailable.",
                retryable: true,
                cancellationToken);
            return;
        }
        catch (HttpOperationException)
        {
            await PersistAdapterInfrastructureFailureAsync(
                claimed,
                generation,
                operation,
                adapterStartedTimestamp,
                "KubernetesRequestFailed",
                cancellationToken);
            return;
        }
        catch (HttpRequestException)
        {
            await PersistAdapterInfrastructureFailureAsync(
                claimed,
                generation,
                operation,
                adapterStartedTimestamp,
                "KubernetesTransportFailed",
                cancellationToken);
            return;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await PersistAdapterInfrastructureFailureAsync(
                claimed,
                generation,
                operation,
                adapterStartedTimestamp,
                "KubernetesRequestTimedOut",
                cancellationToken);
            return;
        }

        switch (result)
        {
            case ResourceDeploymentAdapterResult.Succeeded succeeded:
                metrics.RecordCompletion(
                    claimed.ResourceType,
                    operation,
                    adapterStartedTimestamp);
                await PersistSuccessAsync(
                    claimed,
                    generation,
                    operation,
                    succeeded.Endpoints.Select(endpoint => endpoint.ToDomain()),
                    cancellationToken);
                break;
            case ResourceDeploymentAdapterResult.Failed failed:
                metrics.RecordFailure(
                    claimed.ResourceType,
                    operation,
                    adapterStartedTimestamp);
                await PersistFailureAsync(
                    claimed,
                    generation,
                    operation,
                    failed.FailureCode,
                    failed.FailureMessage,
                    failed.Retryable,
                    cancellationToken);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported adapter result '{result.GetType().FullName}'.");
        }
    }

    private async Task PersistAdapterInfrastructureFailureAsync(
        ResourceDeployment claimed,
        long generation,
        ResourceDeploymentOperation operation,
        long adapterStartedTimestamp,
        string failureCode,
        CancellationToken cancellationToken)
    {
        metrics.RecordFailure(
            claimed.ResourceType,
            operation,
            adapterStartedTimestamp);
        logger.LogWarning(
            "Resource deployment {ResourceDeploymentId} adapter reported infrastructure failure {FailureCode}",
            claimed.Id,
            failureCode);
        await PersistFailureAsync(
            claimed,
            generation,
            operation,
            failureCode,
            "Kubernetes could not complete the resource deployment operation.",
            retryable: true,
            cancellationToken);
    }

    private async Task<ResourceDeployment?> TryClaimAsync(
        string tenantId,
        string deploymentId,
        string runtimeCellId,
        long generation,
        ResourceDeploymentOperation operation,
        CancellationToken cancellationToken)
    {
        var current = await dbContext.ResourceDeployments
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
        var replacement = await dbContext.ResourceDeployments.ReplaceOneAsync(
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

    private Task PersistSuccessAsync(
        ResourceDeployment claimed,
        long generation,
        ResourceDeploymentOperation operation,
        IEnumerable<ResourceDeploymentEndpoint> endpoints,
        CancellationToken cancellationToken) =>
        PersistCompletionAsync(
            claimed,
            current => current.Complete(
                generation,
                operation,
                ((ReconcilingResourceDeploymentState)claimed.State).ClaimId,
                endpoints,
                timeProvider.GetUtcNow().UtcDateTime),
            cancellationToken);

    private Task PersistFailureAsync(
        ResourceDeployment claimed,
        long generation,
        ResourceDeploymentOperation operation,
        string failureCode,
        string failureMessage,
        bool retryable,
        CancellationToken cancellationToken)
    {
        var currentAttempt = claimed.State is ReconcilingResourceDeploymentState claim
            ? claim.AttemptCount
            : throw new InvalidOperationException("The resource deployment has no active claim.");
        var failedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        return PersistCompletionAsync(
            claimed,
            current => current.Fail(
                generation,
                operation,
                ((ReconcilingResourceDeploymentState)claimed.State).ClaimId,
                failureCode,
                failureMessage,
                retryable,
                failedAtUtc,
                failedAtUtc.Add(
                    retryDelayPolicy.Calculate(currentAttempt))),
            cancellationToken);
    }

    private async Task PersistCompletionAsync(
        ResourceDeployment claimed,
        Func<ResourceDeployment, ResourceDeployment> transition,
        CancellationToken cancellationToken)
    {
        var persistence = await dbContext.RunInTransactionAsync(
            async (session, ct) =>
            {
                var current = await dbContext.ResourceDeployments
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
                var replacement = await dbContext.ResourceDeployments.ReplaceOneAsync(
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

                switch (completed.State)
                {
                    case PendingResourceDeploymentState:
                        await dbContext.StoreEventAsync(
                            completed.ToCurrentRequestWrite(),
                            session,
                            ct);
                        break;
                    case AppliedResourceDeploymentState applied:
                        await dbContext.StoreEventAsync(
                            completed.ToAppliedEvent(applied.Generation),
                            session,
                            ct);
                        break;
                    case FailedResourceDeploymentState failed:
                        await dbContext.StoreEventAsync(
                            completed.ToFailedEvent(failed),
                            session,
                            ct);
                        break;
                    case DeletedResourceDeploymentState deleted:
                        await dbContext.StoreEventAsync(
                            completed.ToDeletedEvent(deleted.Generation),
                            session,
                            ct);
                        break;
                }

                return Result.Success();
            },
            cancellationToken: cancellationToken);
        if (persistence is not ISuccess)
        {
            logger.LogWarning(
                "Resource deployment {ResourceDeploymentId} completion was not persisted",
                claimed.Id);
        }
    }
}
