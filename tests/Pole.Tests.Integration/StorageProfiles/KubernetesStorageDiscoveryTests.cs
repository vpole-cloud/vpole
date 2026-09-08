using k8s.Models;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Contracts;
using Xunit;

namespace Pole.Tests.Integration.StorageProfiles;

public sealed class KubernetesStorageDiscoveryTests
{
    [Fact]
    public void PersistentVolumeClaimOwner_PrefersCanonicalResourceTypeAnnotation()
    {
        var claim = CreateClaim(
            resourceTypeLabel: "Widget",
            resourceTypeAnnotation: "third-party/widget");

        var mapped = KubernetesStorageDiscovery.MapPersistentVolumeClaim(claim);

        Assert.Equal("third-party/widget", mapped.Owner.ResourceType);
        Assert.Equal("resource-1", mapped.Owner.ResourceId);
    }

    [Fact]
    public void PersistentVolumeClaimOwner_FallsBackToCompatibilityLabel()
    {
        var claim = CreateClaim(
            resourceTypeLabel: "Widget",
            resourceTypeAnnotation: null);

        var mapped = KubernetesStorageDiscovery.MapPersistentVolumeClaim(claim);

        Assert.Equal("Widget", mapped.Owner.ResourceType);
    }

    private static V1PersistentVolumeClaim CreateClaim(
        string resourceTypeLabel,
        string? resourceTypeAnnotation)
    {
        var annotations = resourceTypeAnnotation is null
            ? null
            : new Dictionary<string, string>
            {
                [PoleStorageLabels.ResourceType] = resourceTypeAnnotation
            };
        return new V1PersistentVolumeClaim
        {
            Metadata = new V1ObjectMeta
            {
                Name = "claim-1",
                NamespaceProperty = "pole-test",
                Labels = new Dictionary<string, string>
                {
                    [PoleStorageLabels.ResourceType] = resourceTypeLabel,
                    [PoleStorageLabels.ResourceId] = "resource-1"
                },
                Annotations = annotations
            },
            Spec = new V1PersistentVolumeClaimSpec()
        };
    }
}
