using Pole.Features.Core.Kubernetes;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using k8s.Models;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Kubernetes;

public abstract record WebServiceDeploymentMetadata(
    long Revision,
    DateTime RequestedAtUtc);

public sealed record WebServiceReleaseDeploymentMetadata(
    string ReleaseId,
    long Revision,
    DateTime RequestedAtUtc)
    : WebServiceDeploymentMetadata(Revision, RequestedAtUtc)
{
    public static WebServiceReleaseDeploymentMetadata Create(
        string releaseId,
        long revision,
        DateTime requestedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);
        if (requestedAtUtc == default)
            throw new ArgumentException("The request time is required.", nameof(requestedAtUtc));

        return new(releaseId.Trim(), revision, requestedAtUtc);
    }
}

public sealed record WebServiceResourceDeploymentMetadata(
    string ReleaseId,
    string ResourceDeploymentId,
    string RuntimeCellId,
    string DeploymentTargetId,
    long DesiredGeneration,
    long Revision,
    DateTime RequestedAtUtc)
    : WebServiceDeploymentMetadata(Revision, RequestedAtUtc)
{
    public static WebServiceResourceDeploymentMetadata Create(
        string releaseId,
        string resourceDeploymentId,
        string runtimeCellId,
        string deploymentTargetId,
        long desiredGeneration,
        long logicalRevision,
        DateTime requestedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceDeploymentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeCellId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentTargetId);
        ArgumentOutOfRangeException.ThrowIfLessThan(desiredGeneration, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(logicalRevision, 1);
        if (requestedAtUtc == default)
            throw new ArgumentException("The request time is required.", nameof(requestedAtUtc));

        return new(
            releaseId.Trim(),
            resourceDeploymentId.Trim(),
            runtimeCellId.Trim(),
            deploymentTargetId.Trim(),
            desiredGeneration,
            logicalRevision,
            requestedAtUtc);
    }
}

public readonly record struct WebServiceInKubernetes(
    WebService WebService,
    string Namespace,
    ResourceDeploymentIngressConfiguration IngressConfiguration,
    ResourceDeploymentComputeConfiguration ComputeConfiguration,
    int FixedReplicas,
    WorkloadIdentityLabels PlacementIdentity)
{
    public static WebServiceInKubernetes Create(
        WebService webService,
        string kubernetesNamespace,
        ResourceDeploymentIngressConfiguration ingressConfiguration,
        ResourceDeploymentComputeConfiguration computeConfiguration,
        int fixedReplicas,
        WorkloadIdentityLabels placementIdentity)
    {
        ArgumentNullException.ThrowIfNull(webService);
        ArgumentException.ThrowIfNullOrWhiteSpace(kubernetesNamespace);
        ArgumentNullException.ThrowIfNull(ingressConfiguration);
        ArgumentNullException.ThrowIfNull(computeConfiguration);
        ArgumentOutOfRangeException.ThrowIfLessThan(fixedReplicas, 1);

        return new(
            webService,
            kubernetesNamespace.Trim(),
            ingressConfiguration,
            computeConfiguration,
            fixedReplicas, placementIdentity);
    }

    private Dictionary<string, string> IdentityLabels() => PlacementIdentity.ToDictionary();

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
    public V1Deployment ToDeployment(WebServiceDeploymentMetadata? deploymentMetadata = null) => new ()
    {
        ApiVersion = "apps/v1",
        Kind = "Deployment",
        Metadata = new V1ObjectMeta()
        {
            Name = WebService.Name,
            NamespaceProperty = Namespace,
            Labels = IdentityLabels()
        },
        Spec = new V1DeploymentSpec()
        {
            Replicas = WebService.Spec.Autoscaling?.MinReplicas ?? FixedReplicas,
            Selector = new V1LabelSelector()
            {
                MatchLabels = IdentityLabels()
            },
            Template = new V1PodTemplateSpec()
            {
                Metadata = new V1ObjectMeta()
                {
                    Labels = IdentityLabels(),
                    Annotations = BuildPodTemplateAnnotations(deploymentMetadata),
                },
                Spec = BuildPodSpec()

            }
        }
    };

    private V1PodSpec BuildPodSpec()
    {
        var podSpec = new V1PodSpec
        {
            ImagePullSecrets = WebService.Spec.Image.SecretName is null
                ? null
                : [new V1LocalObjectReference { Name = WebService.Spec.Image.SecretName }],
            Containers =
            [
                new V1Container
                {
                    Name = WebService.Name,
                    Image = WebService.Spec.Image.FullImageName,
                    Ports = [new V1ContainerPort { ContainerPort = 80 }],
                    EnvFrom =
                    [
                        new V1EnvFromSource
                        {
                            ConfigMapRef = new V1ConfigMapEnvSource
                            {
                                Name = ConfigMapName
                            }
                        }
                    ],
                    StartupProbe = WebService.Spec.Probes.Startup.ToV1Probe(),
                    LivenessProbe = WebService.Spec.Probes.Liveness.ToV1Probe(),
                    ReadinessProbe = WebService.Spec.Probes.Readiness.ToV1Probe(),
                    Resources = ComputeConfiguration.ToKubernetesCompute().ToResourceRequirements()
                }
            ]
        };
        ComputeConfiguration.ToKubernetesCompute().ApplyTo(podSpec);
        return podSpec;
    }
    public V1Service ToService() => new()
    {
        ApiVersion = "v1",
        Kind = "Service",
        Metadata = new V1ObjectMeta()
        {
            Name = ServiceName,
            NamespaceProperty = Namespace,
            Labels = IdentityLabels(),
            Annotations = _defaultAnnotations,
        },
        Spec = new V1ServiceSpec()
        {
            Type = "ClusterIP",
            Selector = IdentityLabels(),
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
            Labels = IdentityLabels(),
            Annotations = _defaultAnnotations,
        },
        Data = WebService.Spec.Config.EnvironmentVariables.ToDictionary(),
    };

    public V1Ingress ToIngress()
    {
        var serviceName = ServiceName;
        var servicePort = GetIngressServicePort(WebService);
        var hostnames = IngressConfiguration.Hostnames;
        return new V1Ingress
        {
            ApiVersion = "networking.k8s.io/v1",
            Kind = "Ingress",
            Metadata = new V1ObjectMeta
            {
                Name = IngressName,
                NamespaceProperty = Namespace,
                Labels = IdentityLabels(),
                Annotations = _defaultAnnotations.Concat(new Dictionary<string, string>
                {
                    ["cert-manager.io/cluster-issuer"] = IngressConfiguration.ClusterIssuerName,
                }).ToDictionary(),
            },
            Spec = new V1IngressSpec
            {
                IngressClassName = IngressConfiguration.IngressClassName,
                Rules = hostnames
                    .Select(hostname => new V1IngressRule
                    {
                        Host = hostname,
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
                                            Name = serviceName,
                                            Port = new V1ServiceBackendPort
                                            {
                                                Number = servicePort
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    })
                    .ToList(),
                Tls = new List<V1IngressTLS>
                {
                    new()
                    {
                        Hosts = hostnames.ToList(),
                        SecretName = TlsSecretName
                    }
                }
            }
        };
    }
        
    public V1HorizontalPodAutoscaler? ToHorizontalPodAutoscaler() => WebService.Spec.Autoscaling is null ? null : new V1HorizontalPodAutoscaler
    {
        ApiVersion = "autoscaling/v2",
        Kind = "HorizontalPodAutoscaler",
        Metadata = new V1ObjectMeta()
        {
            Name = $"{WebService.Name}-hpa",
            NamespaceProperty = Namespace,
            Labels = IdentityLabels(),
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
            labels = IdentityLabels(),
            annotations = _defaultAnnotations,
            @namespace = Namespace,
        },
        spec = new
        {
            selector = new
            {
                matchLabels = IdentityLabels()
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

    private ImmutableDictionary<string, string> BuildPodTemplateAnnotations(WebServiceDeploymentMetadata? deploymentMetadata)
    {
        if (deploymentMetadata is null)
            return _defaultAnnotations;

        var annotations = _defaultAnnotations
            .SetItem("pole.server/revision", deploymentMetadata.Revision.ToString(CultureInfo.InvariantCulture))
            .SetItem("pole.server/deploymentRequestedAt", deploymentMetadata.RequestedAtUtc.ToString("O", CultureInfo.InvariantCulture));

        switch (deploymentMetadata)
        {
            case WebServiceReleaseDeploymentMetadata release:
                annotations = annotations.SetItem(
                    "pole.server/releaseId",
                    release.ReleaseId);
                break;
            case WebServiceResourceDeploymentMetadata deployment:
                annotations = annotations
                    .SetItem(
                        "pole.server/releaseId",
                        deployment.ReleaseId)
                    .SetItem(
                        "pole.server/resourceDeploymentId",
                        deployment.ResourceDeploymentId)
                    .SetItem(
                        "pole.server/runtimeCellId",
                        deployment.RuntimeCellId)
                    .SetItem(
                        "pole.server/deploymentTargetId",
                        deployment.DeploymentTargetId)
                    .SetItem(
                        "pole.server/deploymentGeneration",
                        deployment.DesiredGeneration.ToString(
                            CultureInfo.InvariantCulture));
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported web service deployment metadata '{deploymentMetadata.GetType().Name}'.");
        }

        return annotations;
    }
        
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
