using Microsoft.Extensions.Logging;
using k8s.Autorest;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Reconciliation;

public sealed class ResourceDeploymentReconciler(
    ClaimResourceDeployment claimResourceDeployment,
    PersistResourceDeploymentSuccess persistResourceDeploymentSuccess,
    PersistResourceDeploymentFailure persistResourceDeploymentFailure,
    IResourcePlacementLookup placementLookup,
    ResolveResourceDeploymentAdapter resolveResourceDeploymentAdapter,
    ResourceDeploymentMetrics metrics,
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
        var claimed = await claimResourceDeployment(
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

        var (adapter, placementCapability) = resolveResourceDeploymentAdapter(
            claimed.ResourceType);
        if (placementCapability != claimed.PlacementCapability)
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

    private async Task PersistSuccessAsync(
        ResourceDeployment claimed,
        long generation,
        ResourceDeploymentOperation operation,
        IEnumerable<ResourceDeploymentEndpoint> endpoints,
        CancellationToken cancellationToken)
    {
        var result = await persistResourceDeploymentSuccess(
            claimed,
            generation,
            operation,
            endpoints.ToArray(),
            cancellationToken);
        LogPersistenceFailure(result, claimed.Id);
    }

    private async Task PersistFailureAsync(
        ResourceDeployment claimed,
        long generation,
        ResourceDeploymentOperation operation,
        string failureCode,
        string failureMessage,
        bool retryable,
        CancellationToken cancellationToken)
    {
        var result = await persistResourceDeploymentFailure(
            claimed,
            generation,
            operation,
            failureCode,
            failureMessage,
            retryable,
            cancellationToken);
        LogPersistenceFailure(result, claimed.Id);
    }

    private void LogPersistenceFailure(
        Result result,
        string deploymentId)
    {
        if (result is not ISuccess)
        {
            logger.LogWarning(
                "Resource deployment {ResourceDeploymentId} completion was not persisted",
                deploymentId);
        }
    }
}
