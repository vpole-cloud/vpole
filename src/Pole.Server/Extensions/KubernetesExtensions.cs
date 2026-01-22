using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Server.Features.WebServices.Common;

namespace Pole.Server.Extensions;

public static class KubernetesExtensions
{
    public static bool IsAlreadyExists(HttpOperationException ex)
        => ex.Response?.StatusCode == HttpStatusCode.Conflict;

    public static async Task CreateOrIgnoreAsync(Func<Task> createCall)
    {
        try
        {
            await createCall();
        }
        catch (HttpOperationException ex) when (IsAlreadyExists(ex))
        {
            // create-only semantics
        }
    }
    
    extension (IKubernetes kubernetes)
    {
    }
    
    
}

public static class WebServiceExtensions
{
    extension(WebService webService)
    {
        public WebServiceInKubernetes ToKubernetes() => new(webService);
    }

    extension(ProbeConfig probeConfig)
    {
        public V1Probe ToV1Probe() => new()
        {
            HttpGet = new V1HTTPGetAction
            {
                Path = probeConfig.Path,
                Port = probeConfig.Port
            },
            InitialDelaySeconds = probeConfig.InitialDelaySeconds,
            PeriodSeconds = probeConfig.PeriodSeconds,
            TimeoutSeconds = probeConfig.TimeoutSeconds,
            FailureThreshold = probeConfig.FailureThreshold,
            SuccessThreshold = probeConfig.SuccessThreshold,
        };
    }
}

public readonly record struct WebServiceInKubernetes(WebService WebService)
{
    public string ConfigMapName => $"{WebService.Name}-config";
        public string IngressName => $"{WebService.Name}-ing";
        public string ServiceName => $"{WebService.Name}-svc";
        public string DeploymentName => WebService.Name;
        public string TlsSecretName => $"{WebService.Name}-tls";
        public V1Deployment ToDeployment() => new V1Deployment()
        {
            ApiVersion = "apps/v1",
            Kind = "Deployment",
            Metadata = new V1ObjectMeta()
            {
                Name = WebService.Name,
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
                        Annotations = DefaultAnnotations,
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
                                StartupProbe = WebService.Spec.Probes.Startup.ToV1Probe(),
                                LivenessProbe = WebService.Spec.Probes.Liveness.ToV1Probe(),
                                ReadinessProbe = WebService.Spec.Probes.Readiness.ToV1Probe(),
                                Resources = new V1ResourceRequirements()
                                {
                                    Requests = new Dictionary<string, ResourceQuantity>()
                                    {
                                        ["cpu"] = new ($"{WebService.Spec.CpuCores}"),
                                        ["memory"] = new ($"{WebService.Spec.MemoryMi}Mi")
                                    },
                                    Limits = new Dictionary<string, ResourceQuantity>()
                                    {
                                        ["cpu"] = new ($"{WebService.Spec.CpuCores}"),
                                        ["memory"] = new ($"{WebService.Spec.MemoryMi}Mi")
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
                Labels = new Dictionary<string, string>()
                {
                    { "app", WebService.Name }
                },
                Annotations = DefaultAnnotations,
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
                    Labels = new Dictionary<string, string>
                    {
                        ["app"] = WebService.Name
                    },
                    Annotations = DefaultAnnotations,
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
                Labels = new Dictionary<string, string>()
                {
                    { "app", WebService.Name }
                },
                Annotations = DefaultAnnotations,
            },
            Data = WebService.Spec.Config.EnvironmentVariables,
        };

        public V1Ingress ToIngress(string ingressClassName = "traefik") => new()
        {
            ApiVersion = "networking.k8s.io/v1",
            Kind = "Ingress",
            Metadata = new V1ObjectMeta
            {
                Name = IngressName,
                Labels = new Dictionary<string, string> { ["app"] = WebService.Name },
                Annotations = DefaultAnnotations.Concat(new Dictionary<string, string>
                {
                    ["cert-manager.io/cluster-issuer"] = "letsencrypt-prod",
                    ["nginx.ingress.kubernetes.io/ssl-redirect"] = "true",
                    ["nginx.ingress.kubernetes.io/proxy-body-size"] = "50m",
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
        
        private Dictionary<string, string> DefaultAnnotations => new()
        {
            { "pole.server/version", WebService.Version },
            { "app.kubernetes.io/managed-by", "pole-server" },
            { "pole.server/webAppId", WebService.Id },
        };
        
        private static int GetIngressServicePort(WebService webService)
        {
            // Simple default: first mapping
            var first = webService.Spec.Config.PortMappings.FirstOrDefault();
            if (first is null)
                throw new InvalidOperationException("PortMappings is empty; cannot create Service/Ingress.");

            return first.ServicePort;
        }
}
