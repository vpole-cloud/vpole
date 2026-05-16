namespace Pole.Features.ResourceGroups.Services;

public interface IKubernetesNamespaceGenerator
{
    string GenerateNamespaceName(string tenantId, string resourceGroupId);
}
public class KubernetesNamespaceGenerator(
    KubernetesOptions options)
    : IKubernetesNamespaceGenerator
{
    public string GenerateNamespaceName(string tenantId, string resourceGroupId)
    {
        var tenantSegment = tenantId[..Math.Min(8, tenantId.Length)];
        var groupSegment = resourceGroupId[..Math.Min(8, resourceGroupId.Length)];
        return $"{options.NamespaceNamePrefix}-t-{tenantSegment}-rg-{groupSegment}";
    }
}

public sealed class KubernetesOptions
{
    public const string SectionName = "kubernetes";
    public required string NamespaceNamePrefix { get; set; }
}
