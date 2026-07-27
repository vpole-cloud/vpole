using k8s.Models;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Kubernetes;

public static class RuntimeCellNamespaceLabels
{
    public const string TenantId = "pole.io/tenant-id";
    public const string ResourceGroupId = "pole.io/resource-group-id";
    public const string IsolationScopeId = "pole.io/isolation-scope-id";
    public const string RuntimeCellId = "pole.io/runtime-cell-id";
    public const string DeploymentTargetId = "pole.io/deployment-target-id";
}

public static class RuntimeCellNamespaceManifestBuilder
{
    public static V1Namespace Build(RuntimeCell runtimeCell)
    {
        ArgumentNullException.ThrowIfNull(runtimeCell);

        return new V1Namespace
        {
            ApiVersion = "v1",
            Kind = "Namespace",
            Metadata = new V1ObjectMeta
            {
                Name = runtimeCell.KubernetesNamespace,
                Labels = ExpectedOwnershipLabels(runtimeCell)
            }
        };
    }

    internal static Dictionary<string, string> ExpectedOwnershipLabels(RuntimeCell runtimeCell) =>
        new(StringComparer.Ordinal)
        {
            [RuntimeCellNamespaceLabels.TenantId] = runtimeCell.TenantId,
            [RuntimeCellNamespaceLabels.ResourceGroupId] = runtimeCell.ResourceGroupId,
            [RuntimeCellNamespaceLabels.IsolationScopeId] = runtimeCell.IsolationScopeId,
            [RuntimeCellNamespaceLabels.RuntimeCellId] = runtimeCell.Id,
            [RuntimeCellNamespaceLabels.DeploymentTargetId] = runtimeCell.DeploymentTargetId
        };
}
