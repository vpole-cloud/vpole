using Pole.Features.Core.Kubernetes;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application;
using Pole.Features.RedisServices.Application.Declarations;
using Pole.Features.RedisServices.Application.Mongo;
using Pole.Features.RedisServices.Application.Services;
using Pole.Features.RedisServices.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.RedisServices.Contracts;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.RedisServices.Application.Kubernetes;

public sealed class RedisResourceDeploymentAdapter(
    LoadRedisById loadRedisById,
    IKubernetesClientProvider kubernetesClientProvider)
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

        if (redis.Lifecycle is not ActiveRedisLifecycle)
            return ResourceDeploymentAdapterResult.Failed.Create(
                "RedisNotActive", "The Redis resource is not activated.", retryable: false);

        var kubernetes = await kubernetesClientProvider.GetAsync(
            context.Deployment.Placement.DeploymentTargetId,
            cancellationToken);

        var inKubernetes = redis.ToKubernetes(
            context.Deployment.Placement.KubernetesNamespace,
            context.Deployment.Placement.Storage!,
            context.Deployment.Placement.Compute,
            new WorkloadIdentityLabels(
                context.Deployment.TenantId,
                context.Deployment.ResourceGroupId,
                context.Deployment.IsolationScopeId,
                context.Deployment.RuntimeCellId,
                context.Deployment.Placement.DeploymentTargetId,
                RedisResourceDeployment.KubernetesResourceTypeLabel,
                context.Deployment.ResourceId,
                context.Deployment.Id));
        var metadata = RedisResourceDeploymentMetadata.Create(
            context.Deployment.Id,
            context.Deployment.RuntimeCellId,
            context.Deployment.Placement.DeploymentTargetId,
            context.Deployment.DesiredGeneration,
            context.Deployment.UpdatedAtUtc);
        var applyResult = await RedisKubernetesResourceManager.ApplyAsync(
            kubernetes,
            inKubernetes,
            metadata,
            cancellationToken);
        if (applyResult is RedisKubernetesApplyFailed)
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                "KubernetesApplyFailed",
                "Kubernetes could not apply the Redis deployment.",
                retryable: true);
        }

        return ResourceDeploymentAdapterResult.Succeeded.Create(
        [
            ResourceDeploymentEndpointDto.Create(
                RedisConnectionEndpoints.RuntimeEndpointKind,
                inKubernetes.ClusterLocalHost,
                port: RedisConnectionEndpoints.Redis.TcpPort,
                scheme: "redis")
        ]);
    }

    public async Task<ResourceDeploymentAdapterResult> DeleteAsync(
        ResourceDeploymentReconciliationContext context,
        CancellationToken cancellationToken = default)
    {
        var validationFailure = ValidateContext(context);
        if (validationFailure is not null)
            return validationFailure;

        var kubernetes = await kubernetesClientProvider.GetAsync(
            context.Deployment.Placement.DeploymentTargetId,
            cancellationToken);

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
            kubernetes,
            redis.Name,
            context.Deployment.Placement.KubernetesNamespace,
            cancellationToken);

        return ResourceDeploymentAdapterResult.Succeeded.Create();
    }

    private static ResourceDeploymentAdapterResult.Failed? ValidateContext(
        ResourceDeploymentReconciliationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(
                context.Deployment.ResourceType.Value,
                RedisResourceDeployment.ResourceType.Value,
                StringComparison.Ordinal))
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                "ResourceTypeMismatch",
                "The resource deployment is not Redis.",
                retryable: false);
        }

        if (context.Deployment.Placement.Storage is null)
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
