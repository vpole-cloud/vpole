using Pole.Features.Core.Kubernetes;
using System.Collections.Immutable;
using System.Globalization;
using k8s.Models;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.RedisServices.Application.Kubernetes;

public sealed record RedisResourceDeploymentMetadata(
    string ResourceDeploymentId,
    string RuntimeCellId,
    string DeploymentTargetId,
    long DesiredGeneration,
    DateTime RequestedAtUtc)
{
    public static RedisResourceDeploymentMetadata Create(
        string resourceDeploymentId,
        string runtimeCellId,
        string deploymentTargetId,
        long desiredGeneration,
        DateTime requestedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceDeploymentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeCellId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentTargetId);
        ArgumentOutOfRangeException.ThrowIfLessThan(desiredGeneration, 1);
        if (requestedAtUtc == default)
            throw new ArgumentException("The request time is required.", nameof(requestedAtUtc));

        return new(
            resourceDeploymentId.Trim(),
            runtimeCellId.Trim(),
            deploymentTargetId.Trim(),
            desiredGeneration,
            requestedAtUtc);
    }
}

public readonly record struct RedisInKubernetes(
    Redis Redis,
    string Namespace,
    ResourceDeploymentStorageConfiguration StorageConfiguration,
    ResourceDeploymentComputeConfiguration ComputeConfiguration,
    WorkloadIdentityLabels PlacementIdentity)
{
    public static RedisInKubernetes Create(
        Redis redis,
        string kubernetesNamespace,
        ResourceDeploymentStorageConfiguration storageConfiguration,
        ResourceDeploymentComputeConfiguration computeConfiguration,
        WorkloadIdentityLabels placementIdentity)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentException.ThrowIfNullOrWhiteSpace(kubernetesNamespace);
        ArgumentNullException.ThrowIfNull(storageConfiguration);
        ArgumentNullException.ThrowIfNull(computeConfiguration);

        return new(
            redis,
            kubernetesNamespace.Trim(),
            storageConfiguration,
            computeConfiguration,
            placementIdentity);
    }

    public string StatefulSetName => Redis.Name;
    public string ServiceName => BuildServiceName(Redis.Name);
    public static string BuildServiceName(string redisName) => $"{redisName}-svc";

    public string ClusterLocalHost =>
        $"{ServiceName}.{Namespace}.svc.cluster.local";

    public V1StatefulSet ToStatefulSet(
        RedisResourceDeploymentMetadata? deploymentMetadata = null) => new()
        {
            ApiVersion = "apps/v1",
            Kind = "StatefulSet",
            Metadata = new V1ObjectMeta
            {
                Name = StatefulSetName,
                NamespaceProperty = Namespace,
                Labels = new Dictionary<string, string>
                (BuildLabels()),
                Annotations = DefaultAnnotations
            },
            Spec = new V1StatefulSetSpec
            {
                ServiceName = ServiceName,
                Replicas = 1,
                PersistentVolumeClaimRetentionPolicy = new V1StatefulSetPersistentVolumeClaimRetentionPolicy
                {
                    WhenDeleted = "Delete",
                    WhenScaled = "Retain"
                },
                Selector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>
                {
                    { "pole.io/resource-id", Redis.Id }
                }
                },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = new Dictionary<string, string>
                        (BuildLabels()),
                        Annotations = BuildPodTemplateAnnotations(deploymentMetadata)
                    },
                    Spec = BuildPodSpec()
                },
                VolumeClaimTemplates = new List<V1PersistentVolumeClaim>
            {
                new()
                {
                    Metadata = new V1ObjectMeta
                    {
                        Name = "redis-data",
                        Labels = new Dictionary<string, string>(BuildLabels()),
                        Annotations = DefaultAnnotations
                    },
                    Spec = new V1PersistentVolumeClaimSpec
                    {
                        AccessModes = new List<string> { "ReadWriteOnce" },
                        StorageClassName = StorageConfiguration.StorageClassName,
                        Resources = new V1VolumeResourceRequirements
                        {
                            Requests = new Dictionary<string, ResourceQuantity>
                            {
                                ["storage"] = new($"{Redis.Spec.Storage.SizeGb}Gi")
                            }
                        }
                    }
                }
            }
            }
        };

    public V1Service ToService() => new()
    {
        ApiVersion = "v1",
        Kind = "Service",
        Metadata = new V1ObjectMeta
        {
            Name = ServiceName,
            NamespaceProperty = Namespace,
            Labels = new Dictionary<string, string>
                (BuildLabels()),
            Annotations = DefaultAnnotations
        },
        Spec = new V1ServiceSpec
        {
            Type = "ClusterIP",
            ClusterIP = "None", // Headless service for StatefulSet
            Selector = new Dictionary<string, string>
            {
                { "pole.io/resource-id", Redis.Id }
            },
            Ports = new List<V1ServicePort>
            {
                new()
                {
                    Name = "redis",
                    Port = 6379,
                    TargetPort = 6379,
                    Protocol = "TCP"
                }
            }
        }
    };

    private ImmutableDictionary<string, string> DefaultAnnotations =>
        new Dictionary<string, string>
        {
            { "app.kubernetes.io/managed-by", "pole-server" },
            { "pole.server/redisId", Redis.Id },
            { PoleStorageLabels.ResourceName, Redis.Name },
            { PoleStorageLabels.ResourceType, RedisResourceDeployment.ResourceType.Value }
        }.ToImmutableDictionary();

    private Dictionary<string, string> BuildPodTemplateAnnotations(
        RedisResourceDeploymentMetadata? deploymentMetadata)
    {
        var annotations = new Dictionary<string, string>(DefaultAnnotations);
        if (deploymentMetadata is null)
            return annotations;

        annotations["pole.server/resourceDeploymentId"] =
            deploymentMetadata.ResourceDeploymentId;
        annotations["pole.server/runtimeCellId"] =
            deploymentMetadata.RuntimeCellId;
        annotations["pole.server/deploymentTargetId"] =
            deploymentMetadata.DeploymentTargetId;
        annotations["pole.server/deploymentGeneration"] =
            deploymentMetadata.DesiredGeneration.ToString(
                CultureInfo.InvariantCulture);
        annotations["pole.server/deploymentRequestedAtUtc"] =
            deploymentMetadata.RequestedAtUtc.ToUniversalTime().ToString(
                "O",
                CultureInfo.InvariantCulture);
        return annotations;
    }

    private Dictionary<string, string> BuildLabels() => PlacementIdentity.ToDictionary();

    private V1PodSpec BuildPodSpec()
    {
        var podSpec = new V1PodSpec
        {
            Containers =
            [
                new V1Container
                {
                    Name = "redis",
                    Image = "redis:8-alpine",
                    Ports =
                    [
                        new V1ContainerPort
                        {
                            ContainerPort = 6379,
                            Name = "redis"
                        }
                    ],
                    VolumeMounts =
                    [
                        new V1VolumeMount
                        {
                            Name = "redis-data",
                            MountPath = "/data"
                        }
                    ],
                    Resources = ComputeConfiguration.ToKubernetesCompute().ToResourceRequirements()
                }
            ]
        };
        ComputeConfiguration.ToKubernetesCompute().ApplyTo(podSpec);
        return podSpec;
    }
}
