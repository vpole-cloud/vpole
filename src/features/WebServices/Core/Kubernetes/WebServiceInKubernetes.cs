using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using k8s.Models;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Kubernetes;

public readonly record struct WebServiceInKubernetes(WebService WebService)
{
    public string ConfigMapName => BuildConfigMapName(WebService.Name);
    public static string BuildConfigMapName(string webServiceName) => $"{webServiceName}-config";
    public string IngressName => BuildIngressName(WebService.Name);
    public static string BuildIngressName(string webServiceName) => $"{webServiceName}-ing";
    public string ServiceName => BuildServiceName(WebService.Name);
    public static string BuildServiceName(string webServiceName) => $"{webServiceName}-svc";
    public string DeploymentName => BuildDeploymentName(WebService.Name);
    public static string BuildDeploymentName(string webServiceName) => webServiceName;
    public string TlsSecretName => BuildTlsSecretName(WebService.Name);
    public static string BuildTlsSecretName(string webServiceName) => $"{webServiceName}-tls";
    public string ServiceMonitorName => BuildServiceMonitorName(WebService.Name);
    public static string BuildServiceMonitorName(string webServiceName) => $"{webServiceName}-sm";
    public string Namespace => WebService.Spec.KubernetesNamespace;
    public V1Deployment ToDeployment() => new ()
    {
        ApiVersion = "apps/v1",
        Kind = "Deployment",
        Metadata = new V1ObjectMeta()
        {
            Name = WebService.Name,
            NamespaceProperty = Namespace,
            Labels = new Dictionary<string, string>()
            {
                { "app", WebService.Name }
            }
        },
        Spec = new V1DeploymentSpec()
        {
            Replicas = WebService.Spec.Replicas,
            Selector = new V1LabelSelector()
            {
                MatchLabels = new Dictionary<string, string>()
                {
                    { "app", WebService.Name }
                }
            },
            Template = new V1PodTemplateSpec()
            {
                Metadata = new V1ObjectMeta()
                {
                    Labels = new Dictionary<string, string>()
                    {
                        { "app", WebService.Name }
                            
                    },
                    Annotations = _defaultAnnotations,
                },
                Spec = new V1PodSpec()
                {
                    ImagePullSecrets = WebService.Spec.Image.SecretName is null ? null : new List<V1LocalObjectReference>
                    {
                        new()
                        {
                            Name = WebService.Spec.Image.SecretName
                        }
                    },
                    Containers =
                    [
                        new V1Container
                        {
                            Name = WebService.Name,
                            Image = WebService.Spec.Image.FullImageName,
                            Ports = new List<V1ContainerPort>()
                            {
                                new()
                                {
                                    ContainerPort = 80
                                }
                            },
                            EnvFrom = [new V1EnvFromSource { ConfigMapRef = new V1ConfigMapEnvSource { Name = ConfigMapName } }],
                            StartupProbe = WebService.Spec.Probes.Startup.ToV1Probe(),
                            LivenessProbe = WebService.Spec.Probes.Liveness.ToV1Probe(),
                            ReadinessProbe = WebService.Spec.Probes.Readiness.ToV1Probe(),
                            Resources = new V1ResourceRequirements()
                            {
                                Requests = new Dictionary<string, ResourceQuantity>()
                                {
                                    ["cpu"] = new ($"{WebService.Spec.Resources.Requests.CpuCores}"),
                                    ["memory"] = new ($"{WebService.Spec.Resources.Requests.MemoryMi}Mi")
                                },
                                Limits = new Dictionary<string, ResourceQuantity>()
                                {
                                    ["cpu"] = new ($"{WebService.Spec.Resources.Limits.CpuCores}"),
                                    ["memory"] = new ($"{WebService.Spec.Resources.Limits.MemoryMi}Mi")
                                },
                            },
                        }
                    ]
                }

            }
        }
    };
    public V1Service ToService() => new()
    {
        ApiVersion = "v1",
        Kind = "Service",
        Metadata = new V1ObjectMeta()
        {
            Name = ServiceName,
            NamespaceProperty = Namespace,
            Labels = new Dictionary<string, string>()
            {
                { "app", WebService.Name }
            },
            Annotations = _defaultAnnotations,
        },
        Spec = new V1ServiceSpec()
        {
            Type = "ClusterIP",
            Selector = new Dictionary<string, string> { ["app"] = WebService.Name },
            Ports = WebService.Spec.Config.PortMappings.Select((p, idx) => new V1ServicePort
            {
                Name = $"p{idx}-{p.ServicePort}", // recommended: names for multi-port services
                Port = p.ServicePort,
                TargetPort = p.TargetPort,
                Protocol = p.Protocol,
            }).ToList()
        }
    };
        
    public V1Secret? ToImagePullSecret(string username = "", string password = "")
    {
        var rs = WebService.Spec.Image.SecretName;
        if (rs is null) return null;

        // docker config json format
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

        var dockerConfig = new
        {
            auths = new Dictionary<string, object>
            {
                [WebService.Spec.Image.Server] = new
                {
                    username,
                    password,
                    auth
                }
            }
        };

        var json = JsonSerializer.Serialize(dockerConfig);
        var bytes = Encoding.UTF8.GetBytes(json);

        return new V1Secret
        {
            ApiVersion = "v1",
            Kind = "Secret",
            Metadata = new V1ObjectMeta
            {
                Name = WebService.Spec.Image.SecretName,
                NamespaceProperty = Namespace,
                Labels = new Dictionary<string, string>
                {
                    ["app"] = WebService.Name
                },
                Annotations = _defaultAnnotations,
            },
            Type = "kubernetes.io/dockerconfigjson",
            Data = new Dictionary<string, byte[]>
            {
                [".dockerconfigjson"] = bytes
            }
        };
    }

    public V1ConfigMap ToConfigMap() => new()
    {
        ApiVersion = "v1",
        Kind = "ConfigMap",
        Metadata = new V1ObjectMeta()
        {
            Name = ConfigMapName,
            NamespaceProperty = Namespace,
            Labels = new Dictionary<string, string>()
            {
                { "app", WebService.Name }
            },
            Annotations = _defaultAnnotations,
        },
        Data = WebService.Spec.Config.EnvironmentVariables.ToDictionary(),
    };

    public V1Ingress ToIngress(string ingressClassName = "traefik") => new()
    {
        ApiVersion = "networking.k8s.io/v1",
        Kind = "Ingress",
        Metadata = new V1ObjectMeta
        {
            Name = IngressName,
            NamespaceProperty = Namespace,
            Labels = new Dictionary<string, string> { ["app"] = WebService.Name },
            Annotations = _defaultAnnotations.Concat(new Dictionary<string, string>
            {
                ["cert-manager.io/cluster-issuer"] = "letsencrypt-prod",
            }).ToDictionary(),
        },
        Spec = new V1IngressSpec
        {
            IngressClassName = ingressClassName,
            Rules = new List<V1IngressRule>
            {
                new()
                {
                    Host = WebService.DnsRecord.Domain, // e.g. myapp.vpole.space
                    Http = new V1HTTPIngressRuleValue
                    {
                        Paths = new List<V1HTTPIngressPath>
                        {
                            new()
                            {
                                Path = "/",
                                PathType = "Prefix",
                                Backend = new V1IngressBackend
                                {
                                    Service = new V1IngressServiceBackend
                                    {
                                        Name = ServiceName,
                                        Port = new V1ServiceBackendPort
                                        {
                                            Number = GetIngressServicePort(WebService)
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            },
            Tls = new List<V1IngressTLS>
            {
                new()
                {
                    Hosts = new List<string> { WebService.DnsRecord.Domain },
                    SecretName = TlsSecretName
                }
            }
        }
    };
        
    public V1HorizontalPodAutoscaler? ToHorizontalPodAutoscaler() => WebService.Spec.Autoscaling is null ? null : new V1HorizontalPodAutoscaler
    {
        ApiVersion = "autoscaling/v2",
        Kind = "HorizontalPodAutoscaler",
        Metadata = new V1ObjectMeta()
        {
            Name = $"{WebService.Name}-hpa",
            NamespaceProperty = Namespace,
            Labels = new Dictionary<string, string>()
            {
                { "app", WebService.Name }
            },
            Annotations = _defaultAnnotations,
        },
        Spec = new V1HorizontalPodAutoscalerSpec()
        {
            ScaleTargetRef = new V1CrossVersionObjectReference()
            {
                ApiVersion = "apps/v1",
                Kind = "Deployment",
                Name = WebService.Name
            },
            MinReplicas = WebService.Spec.Autoscaling.MinReplicas,
            MaxReplicas = WebService.Spec.Autoscaling.MaxReplicas,
        }
    };
        
    public object ToServiceMonitor() => new
    {
        apiVersion = "monitoring.coreos.com/v1",
        kind = "ServiceMonitor",
        metadata = new
        {
            name = ServiceMonitorName,
            labels = new Dictionary<string, string>
            {
                { "app", WebService.Name }
            },
            annotations = _defaultAnnotations,
            @namespace = Namespace,
        },
        spec = new
        {
            selector = new
            {
                matchLabels = new Dictionary<string, string>
                {
                    { "app", DeploymentName }
                }
            },
            namespaceSelector = new
            {
                matchNames = new[] { Namespace },
            },
            endpoints = new[]
            {
                new
                {
                    port = GetServicePortName(WebService, 0),
                    path = "/metrics",
                    interval = "30s",
                    scrapeTimeout = "10s"
                }
            }
        }
    };
        
    private readonly ImmutableDictionary<string, string> _defaultAnnotations =
        new Dictionary<string, string>
        {
            { "app.kubernetes.io/managed-by", "pole-server" },
            { "pole.server/webServiceId", WebService.Id },
        }.ToImmutableDictionary();
        
    private static int GetIngressServicePort(WebService webService)
    {
        // Simple default: first mapping
        return webService.Spec.Config.PortMappings.FirstOrDefault()?.ServicePort ?? throw new InvalidOperationException("PortMappings is empty; cannot create Service/Ingress.");
    }

    private static string GetServicePortName(WebService webService, int index)
    {
        var portMapping = webService.Spec.Config.PortMappings.ElementAtOrDefault(index)
                          ?? throw new InvalidOperationException("PortMappings is empty; cannot create Service resources.");
        return $"p{index}-{portMapping.ServicePort}";
    }
}
