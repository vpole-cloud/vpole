namespace Pole.Features.Core.Kubernetes;

/// <summary>Immutable authorization identity shared by pod, Service and policy selectors.</summary>
public sealed record WorkloadIdentityLabels(
    string TenantId,
    string ResourceGroupId,
    string IsolationScopeId,
    string RuntimeCellId,
    string DeploymentTargetId,
    string ResourceType,
    string ResourceId,
    string ResourceDeploymentId
)
{
    public Dictionary<string, string> ToDictionary() => new(StringComparer.Ordinal)
    {
        ["pole.io/tenant-id"] = TenantId,
        ["pole.io/resource-group-id"] = ResourceGroupId,
        ["pole.io/isolation-scope-id"] = IsolationScopeId,
        ["pole.io/runtime-cell-id"] = RuntimeCellId,
        ["pole.io/deployment-target-id"] = DeploymentTargetId,
        ["pole.io/resource-type"] = ResourceType,
        ["pole.io/resource-id"] = ResourceId,
        ["pole.io/resource-deployment-id"] = ResourceDeploymentId,
    };
}
