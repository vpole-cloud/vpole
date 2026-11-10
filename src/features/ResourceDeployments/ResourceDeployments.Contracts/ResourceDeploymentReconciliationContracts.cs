using System.Collections.Immutable;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Contracts;

public sealed record ResourceDeploymentResolvedPlacementSnapshot(
    string DeploymentTargetId,
    long TargetWorkloadConfigurationRevision,
    string KubernetesNamespace,
    ResourceDeploymentIngressConfiguration? Ingress,
    ResourceDeploymentStorageConfiguration? Storage,
    ResourceDeploymentComputeConfiguration Compute
);

public sealed record ResourceDeploymentReconciliationSnapshot(
    string Id,
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId,
    string ResourceGroupId,
    string IsolationScopeId,
    string RuntimeCellId,
    long ResourceRevision,
    int FixedReplicas,
    long DesiredGeneration,
    DateTime UpdatedAtUtc,
    ResourceDeploymentResolvedPlacementSnapshot Placement
);

public sealed record ResourceDeploymentReconciliationContext(
    ResourceDeploymentReconciliationSnapshot Deployment
);

public interface IResourceDeploymentAdapter
{
    Task<ResourceDeploymentAdapterResult> ApplyAsync(
        ResourceDeploymentReconciliationContext context,
        CancellationToken cancellationToken = default);

    Task<ResourceDeploymentAdapterResult> DeleteAsync(
        ResourceDeploymentReconciliationContext context,
        CancellationToken cancellationToken = default);
}

public abstract record ResourceDeploymentAdapterResult
{
    private ResourceDeploymentAdapterResult()
    {
    }

    public sealed record Succeeded(
        ImmutableList<ResourceDeploymentEndpointDto> Endpoints
    ) : ResourceDeploymentAdapterResult
    {
        public static Succeeded Create(IEnumerable<ResourceDeploymentEndpointDto>? endpoints = null) =>
            new((endpoints ?? []).ToImmutableList());
    }

    public sealed record Failed(
        string FailureCode,
        string FailureMessage,
        bool Retryable
    ) : ResourceDeploymentAdapterResult
    {
        public static Failed Create(
            string failureCode,
            string failureMessage,
            bool retryable = true)
        {
            if (string.IsNullOrWhiteSpace(failureCode))
                throw new ArgumentException("Failure code is required.", nameof(failureCode));
            if (string.IsNullOrWhiteSpace(failureMessage))
                throw new ArgumentException("Failure message is required.", nameof(failureMessage));

            return new Failed(failureCode.Trim(), failureMessage.Trim(), retryable);
        }
    }
}

public sealed record ResourceDeploymentAdapterDescriptor(
    ResourceTypeId ResourceType,
    ResourcePlacementCapability PlacementCapability,
    Type AdapterType
)
{
    public static ResourceDeploymentAdapterDescriptor Create<TAdapter>(
        ResourceTypeId resourceType,
        ResourcePlacementCapability placementCapability)
        where TAdapter : class, IResourceDeploymentAdapter => new(resourceType, placementCapability, typeof(TAdapter));
}
