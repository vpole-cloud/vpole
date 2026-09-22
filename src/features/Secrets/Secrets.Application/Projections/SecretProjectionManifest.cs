using System.Text;
using k8s.Models;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Projections;

public static class SecretProjectionManifest
{
    public static V1Secret Create(
        SecretProjection projection,
        SecretBinding binding,
        IReadOnlyDictionary<string, string> values)
    {
        var data = binding.KeyMappings.IsEmpty
            ? values.ToDictionary(x => x.Key, x => Encoding.UTF8.GetBytes(x.Value))
            : binding.KeyMappings.ToDictionary(x => x.Value, x => Encoding.UTF8.GetBytes(values[x.Key]));
        return new V1Secret
        {
            ApiVersion = "v1",
            Kind = "Secret",
            Type = projection.ProjectionType == SecretProjectionType.BasicAuthentication ? "kubernetes.io/basic-auth" : "Opaque",
            Metadata = new V1ObjectMeta
            {
                Name = projection.KubernetesName,
                NamespaceProperty = projection.KubernetesNamespace,
                Labels = new Dictionary<string, string>
                {
                    ["pole.io/tenant-id"] = projection.TenantId,
                    ["pole.io/resource-group-id"] = projection.ResourceGroupId,
                    ["pole.io/isolation-scope-id"] = projection.IsolationScopeId,
                    ["pole.io/secret-id"] = projection.SecretId,
                    ["pole.io/secret-binding-id"] = projection.BindingId,
                    ["pole.io/resource-id"] = projection.ConsumerId,
                    ["pole.io/resource-deployment-id"] = projection.DeploymentId,
                    ["pole.io/runtime-cell-id"] = projection.RuntimeCellId,
                    ["pole.io/deployment-target-id"] = projection.DeploymentTargetId
                },
                Annotations = new Dictionary<string, string>
                {
                    ["pole.io/secret-version"] = projection.DesiredVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["pole.io/secret-revision"] = projection.DesiredRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["pole.io/resource-type"] = projection.ConsumerType
                }
            },
            Data = data
        };
    }
}
