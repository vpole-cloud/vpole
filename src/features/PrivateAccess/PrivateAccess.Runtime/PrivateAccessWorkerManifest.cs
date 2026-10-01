using System.Globalization;
using System.Net;
using k8s.Models;
using Pole.Workloads;

namespace Pole.Features.PrivateAccess.Runtime;

public sealed record PrivateAccessWorkerDestination(
    string TenantId,
    string DeploymentTargetId,
    string RuntimeCellId,
    string ResourceDeploymentId,
    string KubernetesNamespace,
    string ServiceName,
    NetworkPort ServicePort,
    NetworkPort TargetPort);

public sealed record PrivateAccessWorkerSettings(
    string Namespace,
    string Image,
    Uri BrokerUri,
    IReadOnlyList<IPAddress> BrokerAddresses);

/// <summary>
/// Workers live outside workload runtime cells: existing same-cell ingress must never
/// turn the ability to reach one endpoint into access to its neighbouring workloads.
/// </summary>
public static class PrivateAccessWorkerManifest
{
    public const string SessionLabel = "pole.io/private-access-session-id";
    public const string TargetLabel = "pole.io/deployment-target-id";
    public const string WorkerNamespaceLabel = "pole.io/private-access-workers";
    public const string TenantLabel = "pole.io/tenant-id";
    public const string RuntimeCellLabel = "pole.io/runtime-cell-id";
    public const string DeploymentLabel = "pole.io/resource-deployment-id";
    public const string ExpiresAnnotation = "pole.io/private-access-expires-at";
    public const string WorkerBaselineName = "pole-private-access-default-deny";

    public static string Name(string sessionId) => $"pole-access-{sessionId}";

    public static V1NetworkPolicy BuildWorkerBaseline(
        string targetId,
        string workerNamespace) => new()
        {
            ApiVersion = "networking.k8s.io/v1",
            Kind = "NetworkPolicy",
            Metadata = new V1ObjectMeta
            {
                Name = WorkerBaselineName,
                NamespaceProperty = workerNamespace,
                Labels = new Dictionary<string, string>
                {
                    [TargetLabel] = targetId,
                    [WorkerNamespaceLabel] = "true"
                }
            },
            Spec = new V1NetworkPolicySpec
            {
                PodSelector = new V1LabelSelector(),
                PolicyTypes = ["Ingress", "Egress"],
                Ingress = [],
                Egress = []
            }
        };

    public static V1Namespace BuildNamespace(
        string targetId,
        PrivateAccessWorkerSettings settings) => new()
        {
            ApiVersion = "v1",
            Kind = "Namespace",
            Metadata = new V1ObjectMeta
            {
                Name = settings.Namespace,
                Labels = new Dictionary<string, string>
                {
                    [TargetLabel] = targetId,
                    [WorkerNamespaceLabel] = "true",
                    ["pod-security.kubernetes.io/enforce"] = "restricted"
                }
            }
        };

    public static V1Job BuildJob(
        string sessionId,
        PrivateAccessWorkerDestination destination,
        PrivateAccessWorkerSettings settings,
        DateTimeOffset expiresAt)
    {
        var labels = Labels(sessionId, destination);
        return new V1Job
        {
            ApiVersion = "batch/v1",
            Kind = "Job",
            Metadata = Metadata(sessionId, destination, settings.Namespace, expiresAt),
            Spec = new V1JobSpec
            {
                BackoffLimit = 0,
                TtlSecondsAfterFinished = 30,
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta { Labels = labels },
                    Spec = new V1PodSpec
                    {
                        RestartPolicy = "Never",
                        AutomountServiceAccountToken = false,
                        HostNetwork = false,
                        HostPID = false,
                        HostIPC = false,
                        SecurityContext = new V1PodSecurityContext
                        {
                            RunAsNonRoot = true,
                            RunAsUser = 65532,
                            RunAsGroup = 65532,
                            FsGroup = 65532,
                            SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" }
                        },
                        Containers =
                        [
                            new V1Container
                            {
                                Name = "proxy",
                                Image = settings.Image,
                                Args = ["worker"],
                                Env =
                                [
                                    new V1EnvVar { Name = "POLE_ACCESS_BROKER", Value = settings.BrokerUri.AbsoluteUri },
                                    new V1EnvVar { Name = "POLE_ACCESS_SESSION", Value = sessionId },
                                    new V1EnvVar { Name = "POLE_ACCESS_SERVICE",
                                        Value = $"{destination.ServiceName}.{destination.KubernetesNamespace}.svc" },
                                    new V1EnvVar { Name = "POLE_ACCESS_PORT", Value = destination.ServicePort.Value.ToString(CultureInfo.InvariantCulture) },
                                    new V1EnvVar { Name = "POLE_ACCESS_CREDENTIAL_FILE", Value = "/run/pole-access/ticket" }
                                ],
                                VolumeMounts = [new V1VolumeMount { Name = "credential", MountPath = "/run/pole-access", ReadOnlyProperty = true }],
                                SecurityContext = new V1SecurityContext
                                {
                                    AllowPrivilegeEscalation = false,
                                    ReadOnlyRootFilesystem = true,
                                    Capabilities = new V1Capabilities { Drop = ["ALL"] }
                                },
                                Resources = new V1ResourceRequirements
                                {
                                    Requests = new Dictionary<string, ResourceQuantity>
                                    {
                                        ["cpu"] = new("10m"),
                                        ["memory"] = new("32Mi")
                                    },
                                    Limits = new Dictionary<string, ResourceQuantity>
                                    {
                                        ["cpu"] = new("250m"),
                                        ["memory"] = new("128Mi")
                                    }
                                }
                            }
                        ],
                        Volumes =
                        [
                            new V1Volume
                            {
                                Name = "credential",
                                Secret = new V1SecretVolumeSource
                                {
                                    SecretName = Name(sessionId),
                                    DefaultMode = 288 // 0440; root-owned volume, pod fsGroup may read.
                                }
                            }
                        ]
                    }
                }
            }
        };
    }

    public static V1NetworkPolicy BuildWorkerPolicy(
        string sessionId,
        PrivateAccessWorkerDestination destination,
        PrivateAccessWorkerSettings settings,
        DateTimeOffset expiresAt) => new()
        {
            ApiVersion = "networking.k8s.io/v1",
            Kind = "NetworkPolicy",
            Metadata = Metadata(sessionId, destination, settings.Namespace, expiresAt),
            Spec = new V1NetworkPolicySpec
            {
                PodSelector = Selector(Labels(sessionId, destination)),
                PolicyTypes = ["Ingress", "Egress"],
                Ingress = [],
                Egress =
                [
                    new V1NetworkPolicyEgressRule
                    {
                        To = [DestinationPeer(destination)],
                        Ports = [Port(destination.TargetPort.Value)]
                    },
                    new V1NetworkPolicyEgressRule
                    {
                        To = settings.BrokerAddresses.Select(address => new V1NetworkPolicyPeer
                        {
                            IpBlock = new V1IPBlock
                            {
                                Cidr = $"{address}/{(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128)}"
                            }
                        }).ToList(),
                        Ports = [Port(settings.BrokerUri.Port)]
                    },
                    new V1NetworkPolicyEgressRule
                    {
                        To =
                        [
                            new V1NetworkPolicyPeer
                            {
                                NamespaceSelector = Selector(new Dictionary<string, string>
                                {
                                    ["kubernetes.io/metadata.name"] = "kube-system"
                                }),
                                PodSelector = Selector(new Dictionary<string, string> { ["k8s-app"] = "kube-dns" })
                            }
                        ],
                        Ports = [Port(53), new V1NetworkPolicyPort { Protocol = "UDP", Port = 53 }]
                    }
                ]
            }
        };

    public static V1NetworkPolicy BuildDestinationPolicy(
        string sessionId,
        PrivateAccessWorkerDestination destination,
        PrivateAccessWorkerSettings settings,
        DateTimeOffset expiresAt) => new()
        {
            ApiVersion = "networking.k8s.io/v1",
            Kind = "NetworkPolicy",
            Metadata = Metadata(sessionId, destination, destination.KubernetesNamespace, expiresAt),
            Spec = new V1NetworkPolicySpec
            {
                PodSelector = DestinationSelector(destination),
                PolicyTypes = ["Ingress"],
                Ingress =
                [
                    new V1NetworkPolicyIngressRule
                    {
                        Ports = [Port(destination.TargetPort.Value)],
                        FromProperty =
                        [
                            new V1NetworkPolicyPeer
                            {
                                NamespaceSelector = Selector(new Dictionary<string, string>
                                {
                                    ["kubernetes.io/metadata.name"] = settings.Namespace,
                                    [TargetLabel] = destination.DeploymentTargetId,
                                    [WorkerNamespaceLabel] = "true"
                                }),
                                PodSelector = Selector(Labels(sessionId, destination))
                            }
                        ]
                    }
                ]
            }
        };

    private static V1NetworkPolicyPeer DestinationPeer(PrivateAccessWorkerDestination destination) => new()
    {
        NamespaceSelector = Selector(new Dictionary<string, string>
        {
            ["kubernetes.io/metadata.name"] = destination.KubernetesNamespace,
            [TenantLabel] = destination.TenantId,
            [RuntimeCellLabel] = destination.RuntimeCellId,
            [TargetLabel] = destination.DeploymentTargetId
        }),
        PodSelector = DestinationSelector(destination)
    };

    private static V1LabelSelector DestinationSelector(PrivateAccessWorkerDestination destination) =>
        Selector(new Dictionary<string, string>
        {
            [TenantLabel] = destination.TenantId,
            [RuntimeCellLabel] = destination.RuntimeCellId,
            [DeploymentLabel] = destination.ResourceDeploymentId
        });

    private static Dictionary<string, string> Labels(
        string sessionId,
        PrivateAccessWorkerDestination destination) => new()
        {
            [SessionLabel] = sessionId,
            [TenantLabel] = destination.TenantId,
            [TargetLabel] = destination.DeploymentTargetId
        };

    private static V1ObjectMeta Metadata(
        string sessionId,
        PrivateAccessWorkerDestination destination,
        string namespaceName,
        DateTimeOffset expiresAt) => new()
        {
            Name = Name(sessionId),
            NamespaceProperty = namespaceName,
            Labels = Labels(sessionId, destination),
            Annotations = new Dictionary<string, string>
            {
                [ExpiresAnnotation] = expiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            }
        };

    private static V1LabelSelector Selector(Dictionary<string, string> labels) => new() { MatchLabels = labels };
    private static V1NetworkPolicyPort Port(int port) => new() { Protocol = "TCP", Port = port };
}
