namespace Pole.Features.ResourceGroups.Application.Kubernetes;

public delegate string GenerateResourceGroupNamespaceName(
    string tenantId,
    string resourceGroupId);

public sealed class KubernetesResourceGroupNamespaceNameGenerator(KubernetesOptions options)
{
    public string Invoke(string tenantId, string resourceGroupId)
    {
        var tenantSegment = tenantId[..Math.Min(8, tenantId.Length)];
        var groupSegment = resourceGroupId[..Math.Min(8, resourceGroupId.Length)];
        return $"{options.NamespaceNamePrefix}-t-{tenantSegment}-rg-{groupSegment}";
    }
}
