namespace Pole.Features.ResourceGroups.Services;

public interface IKubernetesNamespaceGenerator
{
    string GenerateNamespaceName(string resourceGroupId);
}
public class KubernetesNamespaceGenerator(
    KubernetesOptions options)
    : IKubernetesNamespaceGenerator
{
    public string GenerateNamespaceName(string resourceGroupId)
    {
        return $"{options.NamespaceNamePrefix}-{resourceGroupId}";
    }
}

public sealed class KubernetesOptions
{
    public const string SectionName = "kubernetes";
    public required string NamespaceNamePrefix { get; set; }
}