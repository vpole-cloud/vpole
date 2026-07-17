using System.Globalization;
using k8s.Models;
using Pole.Features.Functions.Domain;
using Pole.Functions.Protocol;

namespace Pole.Features.Functions.Runtime.Kubernetes;

public readonly record struct FunctionRevisionInKubernetes(
    FunctionApp App,
    FunctionRevision Revision)
{
    public const int AgentPort = 8080;

    public string Namespace => Revision.Spec.KubernetesNamespace;
    public string ResourceName => BuildResourceName(App.Id, Revision.RevisionNumber);
    public string DeploymentName => ResourceName;
    public string ServiceName => $"{ResourceName}-agent";

    public static string BuildResourceName(string functionAppId, int revisionNumber)
    {
        var id = new string(functionAppId
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .Take(24)
            .ToArray());
        if (id.Length == 0)
            throw new ArgumentException("Function App id must contain an alphanumeric character.", nameof(functionAppId));
        return $"fn-{id}-r{revisionNumber.ToString(CultureInfo.InvariantCulture)}";
    }

    public V1Deployment ToDeployment()
    {
        var selectorLabels = SelectorLabels();
        var labels = Labels();
        var container = new V1Container
        {
            Name = "function-agent",
            Image = Revision.Spec.Image.Reference,
            ImagePullPolicy = Revision.Spec.Image.Digest is null ? "IfNotPresent" : "Always",
            Ports =
            [
                new V1ContainerPort
                {
                    ContainerPort = AgentPort,
                    Name = "agent",
                    Protocol = "TCP"
                }
            ],
            Env = BuildEnvironment(),
            Resources = new V1ResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity>
                {
                    ["cpu"] = new($"{Revision.Spec.Resources.Requests.CpuMillis}m"),
                    ["memory"] = new($"{Revision.Spec.Resources.Requests.MemoryMi}Mi")
                },
                Limits = new Dictionary<string, ResourceQuantity>
                {
                    ["cpu"] = new($"{Revision.Spec.Resources.Limits.CpuMillis}m"),
                    ["memory"] = new($"{Revision.Spec.Resources.Limits.MemoryMi}Mi")
                }
            },
            StartupProbe = HttpProbe(PoleFunctionProtocol.LivenessPath, failureThreshold: 30),
            LivenessProbe = HttpProbe(PoleFunctionProtocol.LivenessPath, failureThreshold: 3),
            ReadinessProbe = HttpProbe(PoleFunctionProtocol.ReadinessPath, failureThreshold: 3),
            SecurityContext = new V1SecurityContext
            {
                AllowPrivilegeEscalation = false,
                ReadOnlyRootFilesystem = true,
                RunAsNonRoot = true,
                Capabilities = new V1Capabilities { Drop = ["ALL"] }
            },
            VolumeMounts = [new V1VolumeMount { Name = "tmp", MountPath = "/tmp" }]
        };

        return new V1Deployment
        {
            ApiVersion = "apps/v1",
            Kind = "Deployment",
            Metadata = Metadata(DeploymentName, labels),
            Spec = new V1DeploymentSpec
            {
                Replicas = 0,
                RevisionHistoryLimit = 1,
                Selector = new V1LabelSelector { MatchLabels = selectorLabels },
                Strategy = new V1DeploymentStrategy { Type = "Recreate" },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta { Labels = labels },
                    Spec = new V1PodSpec
                    {
                        AutomountServiceAccountToken = false,
                        EnableServiceLinks = false,
                        TerminationGracePeriodSeconds = 30,
                        SecurityContext = new V1PodSecurityContext
                        {
                            RunAsNonRoot = true,
                            SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" }
                        },
                        ImagePullSecrets = Revision.Spec.Image.PullSecretName is null
                            ? null
                            : [new V1LocalObjectReference { Name = Revision.Spec.Image.PullSecretName }],
                        Containers = [container],
                        Volumes =
                        [
                            new V1Volume
                            {
                                Name = "tmp",
                                EmptyDir = new V1EmptyDirVolumeSource()
                            }
                        ]
                    }
                }
            }
        };
    }

    public V1Service ToService() => new()
    {
        ApiVersion = "v1",
        Kind = "Service",
        Metadata = Metadata(ServiceName, Labels()),
        Spec = new V1ServiceSpec
        {
            Type = "ClusterIP",
            Selector = SelectorLabels(),
            PublishNotReadyAddresses = false,
            Ports =
            [
                new V1ServicePort
                {
                    Port = AgentPort,
                    Name = "agent",
                    Protocol = "TCP",
                    TargetPort = AgentPort
                }
            ]
        }
    };

    private List<V1EnvVar> BuildEnvironment()
    {
        var environment = Revision.Spec.Configuration.EnvironmentVariables
            .Select(pair => Env(pair.Key, pair.Value))
            .ToList();
        environment.AddRange(Revision.Spec.Configuration.SecretReferences.Select(pair => new V1EnvVar
        {
            Name = pair.Key,
            ValueFrom = new V1EnvVarSource
            {
                SecretKeyRef = new V1SecretKeySelector
                {
                    Name = pair.Value,
                    Key = pair.Key
                }
            }
        }));
        environment.AddRange(
        [
            Env("ASPNETCORE_URLS", $"http://+:{AgentPort}"),
            Env("DOTNET_EnableDiagnostics", "0"),
            Env("PoleFunctions__FunctionAppId", App.Id),
            Env("PoleFunctions__RevisionId", Revision.Id),
            Env(
                "PoleFunctions__MaxConcurrencyPerInstance",
                Revision.Spec.Scaling.MaxConcurrencyPerInstance.ToString(CultureInfo.InvariantCulture)),
            Env(
                "PoleFunctions__DefaultHandlerConcurrencyPerInstance",
                Revision.Spec.Scaling.MaxConcurrencyPerInstance.ToString(CultureInfo.InvariantCulture))
        ]);
        environment.AddRange(Revision.Spec.Handlers.Select(handler => Env(
            $"PoleFunctions__HandlerConcurrencyPerInstance__{handler.Name}",
            handler.MaxConcurrencyPerInstance.ToString(CultureInfo.InvariantCulture))));
        return environment;
    }

    private static V1EnvVar Env(string name, string value) => new() { Name = name, Value = value };

    private V1ObjectMeta Metadata(string name, IDictionary<string, string> labels) => new()
    {
        Name = name,
        NamespaceProperty = Namespace,
        Labels = labels,
        Annotations = new Dictionary<string, string>
        {
            ["app.kubernetes.io/managed-by"] = "pole-server"
        }
    };

    private Dictionary<string, string> Labels() => new()
    {
        ["app.kubernetes.io/name"] = "pole-function",
        ["app.kubernetes.io/instance"] = ResourceName,
        ["pole.io/tenant-id"] = App.TenantId,
        ["pole.io/resource-group-id"] = App.ResourceGroupId,
        ["pole.io/resource-type"] = "function-app",
        ["pole.io/resource-id"] = App.Id,
        ["pole.io/function-app-id"] = App.Id,
        ["pole.io/function-revision"] = Revision.RevisionNumber.ToString(CultureInfo.InvariantCulture),
        ["pole.io/function-revision-id"] = Revision.Id
    };

    private Dictionary<string, string> SelectorLabels() => new()
    {
        ["pole.io/function-app-id"] = App.Id,
        ["pole.io/function-revision-id"] = Revision.Id
    };

    private static V1Probe HttpProbe(string path, int failureThreshold) => new()
    {
        HttpGet = new V1HTTPGetAction { Path = path, Port = AgentPort, Scheme = "HTTP" },
        PeriodSeconds = 2,
        TimeoutSeconds = 1,
        FailureThreshold = failureThreshold,
        SuccessThreshold = 1
    };
}
