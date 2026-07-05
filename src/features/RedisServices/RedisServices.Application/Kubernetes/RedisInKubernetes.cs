using System.Collections.Immutable;
using k8s.Models;
using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Application.Kubernetes;

public readonly record struct RedisInKubernetes(Redis Redis)
{
    public string StatefulSetName => Redis.Name;
    public string Namespace => Redis.Spec.KubernetesNamespace;
    public string ServiceName => BuildServiceName(Redis.Name);
    public static string BuildServiceName(string redisName) => $"{redisName}-svc";
    
    public V1StatefulSet ToStatefulSet() => new()
    {
        ApiVersion = "apps/v1",
        Kind = "StatefulSet",
        Metadata = new V1ObjectMeta
        {
            Name = StatefulSetName,
            NamespaceProperty = Namespace,
            Labels = new Dictionary<string, string>
            {
                { "app", Redis.Name },
                { "component", "redis" }
            }
        },
        Spec = new V1StatefulSetSpec
        {
            ServiceName = ServiceName,
            Replicas = 1,
            Selector = new V1LabelSelector
            {
                MatchLabels = new Dictionary<string, string>
                {
                    { "app", Redis.Name }
                }
            },
            Template = new V1PodTemplateSpec
            {
                Metadata = new V1ObjectMeta
                {
                    Labels = new Dictionary<string, string>
                    {
                        { "app", Redis.Name },
                        { "component", "redis" }
                    },
                    Annotations = _defaultAnnotations
                },
                Spec = new V1PodSpec
                {
                    Containers = new List<V1Container>
                    {
                        new()
                        {
                            Name = "redis",
                            Image = "redis:8-alpine",
                            Ports = new List<V1ContainerPort>
                            {
                                new()
                                {
                                    ContainerPort = 6379,
                                    Name = "redis"
                                }
                            },
                            VolumeMounts = new List<V1VolumeMount>
                            {
                                new()
                                {
                                    Name = "redis-data",
                                    MountPath = "/data"
                                }
                            },
                            Resources = new V1ResourceRequirements
                            {
                                Requests = new Dictionary<string, ResourceQuantity>
                                {
                                    ["cpu"] = new($"{Redis.Spec.Resources.Requests.CpuCores}"),
                                    ["memory"] = new($"{Redis.Spec.Resources.Requests.MemoryMi}Mi")
                                },
                                Limits = new Dictionary<string, ResourceQuantity>
                                {
                                    ["cpu"] = new($"{Redis.Spec.Resources.Limits.CpuCores}"),
                                    ["memory"] = new($"{Redis.Spec.Resources.Limits.MemoryMi}Mi")
                                }
                            }
                        }
                    }
                }
            },
            VolumeClaimTemplates = new List<V1PersistentVolumeClaim>
            {
                new()
                {
                    Metadata = new V1ObjectMeta
                    {
                        Name = "redis-data"
                    },
                    Spec = new V1PersistentVolumeClaimSpec
                    {
                        AccessModes = new List<string> { "ReadWriteOnce" },
                        StorageClassName = Redis.Spec.Storage.StorageClassName,
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
            {
                { "app", Redis.Name }
            },
            Annotations = _defaultAnnotations
        },
        Spec = new V1ServiceSpec
        {
            Type = "ClusterIP",
            ClusterIP = "None", // Headless service for StatefulSet
            Selector = new Dictionary<string, string>
            {
                { "app", Redis.Name }
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
    
    private readonly ImmutableDictionary<string, string> _defaultAnnotations =
        new Dictionary<string, string>
        {
            { "app.kubernetes.io/managed-by", "pole-server" },
            { "pole.server/redisId", Redis.Id }
        }.ToImmutableDictionary();
}

