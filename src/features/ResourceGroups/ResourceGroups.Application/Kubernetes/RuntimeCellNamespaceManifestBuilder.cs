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
    internal static IReadOnlySet<string> ReservedLabelKeys { get; } = new HashSet<string>(
        [
            RuntimeCellNamespaceLabels.TenantId,
            RuntimeCellNamespaceLabels.ResourceGroupId,
            RuntimeCellNamespaceLabels.IsolationScopeId,
            RuntimeCellNamespaceLabels.RuntimeCellId,
            RuntimeCellNamespaceLabels.DeploymentTargetId,
        ],
        StringComparer.Ordinal);

    public static V1Namespace Build(
        RuntimeCell runtimeCell,
        IReadOnlyDictionary<string, string>? additionalLabels = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeCell);

        var labels = additionalLabels is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(additionalLabels, StringComparer.Ordinal);
        foreach (var (key, value) in ExpectedOwnershipLabels(runtimeCell))
            labels[key] = value;

        return new V1Namespace
        {
            ApiVersion = "v1",
            Kind = "Namespace",
            Metadata = new V1ObjectMeta
            {
                Name = runtimeCell.KubernetesNamespace,
                Labels = labels,
            },
        };
    }

    internal static Dictionary<string, string> ExpectedOwnershipLabels(RuntimeCell runtimeCell) =>
        new(StringComparer.Ordinal)
        {
            [RuntimeCellNamespaceLabels.TenantId] = runtimeCell.TenantId,
            [RuntimeCellNamespaceLabels.ResourceGroupId] = runtimeCell.ResourceGroupId,
            [RuntimeCellNamespaceLabels.IsolationScopeId] = runtimeCell.IsolationScopeId,
            [RuntimeCellNamespaceLabels.RuntimeCellId] = runtimeCell.Id,
            [RuntimeCellNamespaceLabels.DeploymentTargetId] = runtimeCell.DeploymentTargetId,
        };
}
