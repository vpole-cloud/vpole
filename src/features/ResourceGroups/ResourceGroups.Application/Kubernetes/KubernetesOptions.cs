namespace Pole.Features.ResourceGroups.Application.Kubernetes;

public sealed class KubernetesOptions
{
    public const string SectionName = "kubernetes";
    public required string NamespaceNamePrefix { get; set; }
}
