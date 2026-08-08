using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Persistence;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;

namespace Pole.Features.RedisServices.Application.Kubernetes;

public sealed class RedisResourceDeploymentAdapter(
    LoadRedisById loadRedisById)
    : IResourceDeploymentAdapter
{
    public async Task<ResourceDeploymentAdapterResult> ApplyAsync(
        ResourceDeploymentReconciliationContext context,
        CancellationToken cancellationToken = default)
    {
        var validationFailure = ValidateContext(context);
        if (validationFailure is not null)
            return validationFailure;

        var redis = await loadRedisById(
            context.Deployment.TenantId,
            context.Deployment.ResourceId,
            cancellationToken);
        if (redis is null)
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                "RedisNotFound",
                "The logical Redis resource no longer exists.",
                retryable: false);
        }

        var inKubernetes = redis.ToKubernetes(
            context.Deployment.ResolvedPlacement.KubernetesNamespace,
            context.Deployment.ResolvedPlacement.Storage!,
            context.Deployment.ResolvedPlacement.Compute);
        var metadata = RedisResourceDeploymentMetadata.Create(
            context.Deployment.Id,
            context.Deployment.RuntimeCellId,
            context.Deployment.ResolvedPlacement.DeploymentTargetId,
            context.Deployment.DesiredGeneration,
            context.Deployment.UpdatedAtUtc);
        var applyResult = await RedisKubernetesResourceManager.ApplyAsync(
            context.Kubernetes,
            inKubernetes,
            metadata,
            cancellationToken);
        if (applyResult is Failure)
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                "KubernetesApplyFailed",
                "Kubernetes could not apply the Redis deployment.",
                retryable: true);
        }

        return ResourceDeploymentAdapterResult.Succeeded.Create(
        [
            RedisResourceDeploymentEndpoint.Create(
                inKubernetes.ClusterLocalHost,
                port: 6379)
        ]);
    }

    public async Task<ResourceDeploymentAdapterResult> DeleteAsync(
        ResourceDeploymentReconciliationContext context,
        CancellationToken cancellationToken = default)
    {
        var validationFailure = ValidateContext(context);
        if (validationFailure is not null)
            return validationFailure;

        var redis = await loadRedisById(
            context.Deployment.TenantId,
            context.Deployment.ResourceId,
            cancellationToken);
        if (redis is null)
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                "RedisNotFound",
                "The logical Redis resource no longer exists.",
                retryable: false);
        }

        await RedisKubernetesResourceManager.DeleteAsync(
            context.Kubernetes,
            redis.Name,
            context.Deployment.ResolvedPlacement.KubernetesNamespace,
            cancellationToken);

        return ResourceDeploymentAdapterResult.Succeeded.Create();
    }

    private static ResourceDeploymentAdapterResult.Failed? ValidateContext(
        ResourceDeploymentReconciliationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(
                context.Deployment.ResourceType,
                RedisResourceDeployment.ResourceType.Value,
                StringComparison.Ordinal))
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                "ResourceTypeMismatch",
                "The resource deployment is not Redis.",
                retryable: false);
        }

        if (context.Deployment.ResolvedPlacement.Storage is null)
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                "StorageConfigurationMissing",
                "The Redis placement does not contain resolved storage configuration.",
                retryable: false);
        }

        return null;
    }
}

public static class RedisResourceDeploymentAdapterRegistration
{
    public static ResourceDeploymentAdapterDescriptor Descriptor { get; } =
        ResourceDeploymentAdapterDescriptor.Create<RedisResourceDeploymentAdapter>(
            RedisResourceDeployment.ResourceType,
            ResourcePlacementCapability.SingleTarget);
}
