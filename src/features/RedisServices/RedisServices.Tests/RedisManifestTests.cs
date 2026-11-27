// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using Pole.Features.Core.Kubernetes;
using Pole.Features.RedisServices.Application.Kubernetes;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.StorageProfiles.Contracts;
using Xunit;
using Redis = Pole.Features.RedisServices.Domain.Redis;

namespace Pole.Features.RedisServices.Tests;

[Trait("Layer", "Manifest")]
[Trait("Concern", "Rendering")]
[Trait("Scope", "ManifestOnly")]
[Trait("Feature", "RedisServices")]
public sealed class RedisManifestTests
{
    private const string TenantId = "507f1f77bcf86cd799439011";

    [Fact]
    public void PreparedResourceCannotRenderWorkloadOrServiceManifests()
    {
        var active = CreateSubject();
        var prepared = active with { Redis = active.Redis with { Lifecycle = new PreparedRedisLifecycle() } };
        Assert.Throws<InvalidOperationException>(() => prepared.ToStatefulSet());
        Assert.Throws<InvalidOperationException>(() => prepared.ToService());
    }

    [Fact]
    public void PlacementIdentity_IsStableAcrossRevisions_AndSeparatesRecreatedResources()
    {
        var subject = CreateSubject();
        var identity = new WorkloadIdentityLabels(
            subject.Redis.TenantId,
            subject.Redis.ResourceGroupId,
            "scope-44",
            "cell-44",
            "target-44",
            "Redis",
            subject.Redis.Id,
            "deployment-44");
        subject = subject with { PlacementIdentity = identity };
        var workload = subject.ToStatefulSet();
        var service = subject.ToService();
        Assert.Equal(identity.ToDictionary(), workload.Spec.Template.Metadata.Labels);
        Assert.Equal(identity.ToDictionary(), service.Metadata.Labels);
        Assert.All(
            service.Spec.Selector,
            pair => Assert.Equal(pair.Value, workload.Spec.Template.Metadata.Labels[pair.Key]));
        var revised = subject with { Redis = subject.Redis with { Revision = subject.Redis.Revision + 1 } };
        Assert.Equal(workload.Spec.Selector.MatchLabels, revised.ToStatefulSet().Spec.Selector.MatchLabels);
        Assert.Equal(service.Spec.Selector, revised.ToService().Spec.Selector);
        const string replacementId = "700000000000000000000044";
        var replacement = subject with
        {
            Redis = subject.Redis with { Id = replacementId },
            PlacementIdentity = identity with
            {
                ResourceId = replacementId,
                ResourceDeploymentId = "replacement-deployment-44",
            },
        };
        Assert.Equal(subject.Redis.Name, replacement.Redis.Name);
        Assert.Equal(replacementId, replacement.ToService().Spec.Selector["pole.io/resource-id"]);
        Assert.NotEqual(
            service.Spec.Selector["pole.io/resource-id"],
            replacement.ToStatefulSet().Spec.Template.Metadata.Labels["pole.io/resource-id"]);
        Assert.DoesNotContain("app", service.Spec.Selector.Keys);
        Assert.Equal(subject.Redis.Name, service.Metadata.Annotations[PoleStorageLabels.ResourceName]);
        Assert.Equal(subject.Redis.Name, replacement.ToService().Metadata.Annotations[PoleStorageLabels.ResourceName]);
        var claim = Assert.Single(replacement.ToStatefulSet().Spec.VolumeClaimTemplates);
        Assert.Equal(subject.Redis.Name, claim.Metadata.Annotations[PoleStorageLabels.ResourceName]);
        Assert.DoesNotContain(PoleStorageLabels.ResourceName, workload.Spec.Selector.MatchLabels.Keys);
    }

    [Fact]
    public void ToStatefulSet_UsesRedisImageStorageResourcesAndManagedMetadata()
    {
        var manifest = CreateSubject();

        var statefulSet = manifest.ToStatefulSet(
            RedisResourceDeploymentMetadata.Create(
                "507f1f77bcf86cd799439031",
                "507f1f77bcf86cd799439032",
                "507f1f77bcf86cd799439033",
                desiredGeneration: 4,
                new DateTime(2026, 7, 29, 14, 0, 0, DateTimeKind.Utc)));
        var container = Assert.Single(statefulSet.Spec.Template.Spec.Containers);
        var volumeClaim = Assert.Single(statefulSet.Spec.VolumeClaimTemplates);

        Assert.Equal("apps/v1", statefulSet.ApiVersion);
        Assert.Equal("StatefulSet", statefulSet.Kind);
        Assert.Equal("redis-cache", statefulSet.Metadata.Name);
        Assert.Equal("pole-cache", statefulSet.Metadata.NamespaceProperty);
        Assert.Equal("redis-cache-svc", statefulSet.Spec.ServiceName);
        Assert.Equal(1, statefulSet.Spec.Replicas);
        Assert.Equal("Delete", statefulSet.Spec.PersistentVolumeClaimRetentionPolicy.WhenDeleted);
        Assert.Equal("Retain", statefulSet.Spec.PersistentVolumeClaimRetentionPolicy.WhenScaled);
        Assert.Equal("redis:8-alpine", container.Image);
        Assert.Equal("redis", container.Name);
        Assert.Equal(6379, Assert.Single(container.Ports).ContainerPort);
        Assert.Equal("/data", Assert.Single(container.VolumeMounts).MountPath);
        Assert.Equal("redis-data", volumeClaim.Metadata.Name);
        Assert.Equal(manifest.Redis.Name, volumeClaim.Metadata.Annotations[PoleStorageLabels.ResourceName]);
        Assert.Equal("fast-cache", volumeClaim.Spec.StorageClassName);
        Assert.Equal(TenantId, volumeClaim.Metadata.Labels["pole.io/tenant-id"]);
        Assert.Equal("rg-1", volumeClaim.Metadata.Labels["pole.io/resource-group-id"]);
        Assert.Equal("Redis", volumeClaim.Metadata.Labels["pole.io/resource-type"]);
        Assert.Equal("pole/redis", statefulSet.Metadata.Annotations["pole.io/resource-type"]);
        Assert.Equal("pole/redis", statefulSet.Spec.Template.Metadata.Annotations["pole.io/resource-type"]);
        Assert.Equal("pole/redis", volumeClaim.Metadata.Annotations["pole.io/resource-type"]);
        Assert.Equal("12Gi", volumeClaim.Spec.Resources.Requests["storage"].ToString());
        Assert.Equal("250m", container.Resources.Requests["cpu"].ToString());
        Assert.Equal("256Mi", container.Resources.Requests["memory"].ToString());
        Assert.Equal("1", container.Resources.Limits["cpu"].ToString());
        Assert.Equal("1Gi", container.Resources.Limits["memory"].ToString());
        Assert.Equal("1", container.Resources.Requests["nvidia.com/gpu"].ToString());
        Assert.Equal("linux", statefulSet.Spec.Template.Spec.NodeSelector["kubernetes.io/os"]);
        Assert.Equal("workload-runtime", statefulSet.Spec.Template.Spec.RuntimeClassName);
        var toleration = Assert.Single(statefulSet.Spec.Template.Spec.Tolerations);
        Assert.Equal("dedicated", toleration.Key);
        Assert.Equal("Exists", toleration.OperatorProperty);
        var nodeAffinity = statefulSet.Spec.Template.Spec.Affinity.NodeAffinity;
        var requiredExpression = Assert.Single(
            Assert.Single(nodeAffinity.RequiredDuringSchedulingIgnoredDuringExecution.NodeSelectorTerms)
                .MatchExpressions);
        Assert.Equal("topology.kubernetes.io/zone", requiredExpression.Key);
        Assert.Equal("In", requiredExpression.OperatorProperty);
        Assert.Equal(["eu-west-1a"], requiredExpression.Values);
        var preferredTerm = Assert.Single(nodeAffinity.PreferredDuringSchedulingIgnoredDuringExecution);
        Assert.Equal(60, preferredTerm.Weight);
        Assert.Equal("node.kubernetes.io/instance-type", Assert.Single(preferredTerm.Preference.MatchExpressions).Key);
        Assert.Equal("pole-server", statefulSet.Spec.Template.Metadata.Annotations["app.kubernetes.io/managed-by"]);
        Assert.Equal("redis-1", statefulSet.Spec.Template.Metadata.Annotations["pole.server/redisId"]);
        Assert.Equal(
            "507f1f77bcf86cd799439031",
            statefulSet.Spec.Template.Metadata.Annotations["pole.server/resourceDeploymentId"]);
        Assert.Equal(
            "507f1f77bcf86cd799439032",
            statefulSet.Spec.Template.Metadata.Annotations["pole.server/runtimeCellId"]);
        Assert.Equal(
            "507f1f77bcf86cd799439033",
            statefulSet.Spec.Template.Metadata.Annotations["pole.server/deploymentTargetId"]);
        Assert.Equal("4", statefulSet.Spec.Template.Metadata.Annotations["pole.server/deploymentGeneration"]);
    }

    [Fact]
    public void ToService_UsesStableHeadlessServiceAndManagedMetadata()
    {
        var manifest = CreateSubject();

        var service = manifest.ToService();
        var port = Assert.Single(service.Spec.Ports);

        Assert.Equal("v1", service.ApiVersion);
        Assert.Equal("Service", service.Kind);
        Assert.Equal("redis-cache-svc", service.Metadata.Name);
        Assert.Equal("pole-cache", service.Metadata.NamespaceProperty);
        Assert.Equal("ClusterIP", service.Spec.Type);
        Assert.Equal("None", service.Spec.ClusterIP);
        Assert.Equal("redis-1", service.Spec.Selector["pole.io/resource-id"]);
        Assert.Equal("redis", port.Name);
        Assert.Equal(6379, port.Port);
        Assert.Equal("6379", port.TargetPort.Value);
        Assert.Equal("TCP", port.Protocol);
        Assert.Equal("pole-server", service.Metadata.Annotations["app.kubernetes.io/managed-by"]);
        Assert.Equal("redis-1", service.Metadata.Annotations["pole.server/redisId"]);
        Assert.Equal("Redis", service.Metadata.Labels["pole.io/resource-type"]);
        Assert.Equal("pole/redis", service.Metadata.Annotations["pole.io/resource-type"]);
    }

    private static RedisInKubernetes CreateSubject() => RedisInKubernetes.Create(
        BuildRedis("redis-1", "redis-cache", new RedisSpec(new Storage(12, "507f1f77bcf86cd799439034"))),
        "pole-cache",
        new ResourceDeploymentStorageConfiguration(
            "507f1f77bcf86cd799439034",
            StorageProfileRevision: 7,
            StorageClassName: "fast-cache"),
        new ResourceDeploymentComputeConfiguration(
            "507f1f77bcf86cd799439035",
            ComputeProfileRevision: 3,
            DeploymentTargetId: "507f1f77bcf86cd799439036",
            DisplayName: "GPU cache",
            new ResourceDeploymentComputeResourceValues(250, 256),
            new ResourceDeploymentComputeResourceValues(1000, 1024),
            ComputeArchitecture.Amd64,
            ExtendedResources:
            [
                new ResourceDeploymentExtendedResource("nvidia.com/gpu", 1),
            ],
            NodeSelector:
            [
                new ResourceDeploymentNodeSelector("kubernetes.io/os", "linux"),
            ],
            RequiredNodeAffinity:
            [
                new ResourceDeploymentNodeAffinityTerm(
                [
                    new ResourceDeploymentNodeAffinityExpression(
                        "topology.kubernetes.io/zone",
                        ComputeNodeAffinityOperator.In,
                        ["eu-west-1a"]),
                ]),
            ],
            PreferredNodeAffinity:
            [
                new ResourceDeploymentPreferredNodeAffinityTerm(
                    60,
                    new ResourceDeploymentNodeAffinityTerm(
                    [
                        new ResourceDeploymentNodeAffinityExpression(
                            "node.kubernetes.io/instance-type",
                            ComputeNodeAffinityOperator.Exists,
                            []),
                    ])),
            ],
            Tolerations:
            [
                new ResourceDeploymentToleration(
                    "dedicated",
                    ComputeTolerationOperator.Exists,
                    Value: null,
                    ComputeTaintEffect.NoSchedule,
                    TolerationSeconds: null),
            ],
            RuntimeClassName: "workload-runtime"),
        new WorkloadIdentityLabels(
            TenantId,
            "rg-1",
            "scope-44",
            "cell-44",
            "target-44",
            "Redis",
            "redis-1",
            "deployment-44"));

    private static Redis BuildRedis(
        string id,
        string name,
        RedisSpec spec) => RedisFactory.Create(
        tenantId: TenantId,
        name: name,
        resourceGroupId: "rg-1",
        spec: spec,
        options: RedisFactory.DefaultCreateOptions with
        {
            Id = id,
            Tags = ImmutableHashSet.Create("cache"),
        });
}
