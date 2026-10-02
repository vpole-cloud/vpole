using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using k8s.Models;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Workloads;

namespace Pole.Features.PrivateAccess.Runtime;

public sealed record PrivateAccessGatewayEndpoint(
    string EndpointId,
    string RuntimeCellId,
    string ResourceDeploymentId,
    string KubernetesNamespace,
    string ServiceName,
    NetworkPort ServicePort,
    NetworkPort TargetPort);

public sealed record PrivateAccessGatewaySettings(
    string Image,
    Uri BrokerUri,
    IReadOnlyList<IPAddress> BrokerAddresses);

/// <summary>One gateway owns one tenant's access to one deployment target.</summary>
public static class PrivateAccessGatewayManifest
{
    public const string TenantLabel = "pole.io/tenant-id";
    public const string TargetLabel = "pole.io/deployment-target-id";
    public const string RuntimeCellLabel = "pole.io/runtime-cell-id";
    public const string DeploymentLabel = "pole.io/resource-deployment-id";
    public const string GatewayLabel = "pole.io/private-access-gateway-id";
    public const string NamespaceLabel = "pole.io/private-access-gateway";
    public const string BaselineName = "pole-private-access-default-deny";
    public const string GatewayName = "pole-access-gateway";
    public const string IdentityFile = "/run/pole-access/identity.pfx";
    public const string GenerationAnnotation = "pole.io/private-access-gateway-generation";

    public static string Namespace(PrivateAccessGatewayIdentity identity) =>
        PrivateAccessGatewayIds.IsCanonicalObjectId(identity.TenantId) &&
        PrivateAccessGatewayIds.IsCanonicalObjectId(identity.DeploymentTargetId) &&
        PrivateAccessGatewayIds.IsCanonicalObjectId(identity.GatewayId)
            ? $"pole-access-{identity.TenantId}-{identity.DeploymentTargetId}"
            : throw new ArgumentException("Gateway identity IDs must be canonical ObjectIds.", nameof(identity));

    public static Dictionary<string, string> Labels(PrivateAccessGatewayIdentity identity) => new()
    {
        [TenantLabel] = identity.TenantId,
        [TargetLabel] = identity.DeploymentTargetId,
        [GatewayLabel] = identity.GatewayId,
        [NamespaceLabel] = "true"
    };

    public static V1Namespace BuildNamespace(PrivateAccessGatewayIdentity identity) => new()
    {
        ApiVersion = "v1",
        Kind = "Namespace",
        Metadata = new V1ObjectMeta
        {
            Name = Namespace(identity),
            Labels = new Dictionary<string, string>
            {
                [TenantLabel] = identity.TenantId,
                [TargetLabel] = identity.DeploymentTargetId,
                [NamespaceLabel] = "true",
                ["pod-security.kubernetes.io/enforce"] = "restricted"
            }
        }
    };

    public static V1NetworkPolicy BuildBaseline(PrivateAccessGatewayIdentity identity) => new()
    {
        ApiVersion = "networking.k8s.io/v1",
        Kind = "NetworkPolicy",
        Metadata = new V1ObjectMeta
        {
            Name = BaselineName,
            NamespaceProperty = Namespace(identity),
            Labels = Labels(identity)
        },
        Spec = new V1NetworkPolicySpec
        {
            PodSelector = new V1LabelSelector(),
            PolicyTypes = ["Ingress", "Egress"],
            Ingress = [],
            Egress = []
        }
    };

    public static V1Deployment BuildDeployment(
        PrivateAccessGatewayIdentity identity,
        PrivateAccessGatewaySettings settings)
    {
        var labels = Labels(identity);
        return new V1Deployment
        {
            ApiVersion = "apps/v1",
            Kind = "Deployment",
            Metadata = new V1ObjectMeta
            {
                Name = GatewayName,
                NamespaceProperty = Namespace(identity),
                Labels = labels
            },
            Spec = new V1DeploymentSpec
            {
                Replicas = 1,
                Strategy = new V1DeploymentStrategy { Type = "Recreate" },
                Selector = new V1LabelSelector { MatchLabels = labels },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = labels,
                        Annotations = new Dictionary<string, string>
                        {
                            [GenerationAnnotation] = identity.Generation.ToString(CultureInfo.InvariantCulture)
                        }
                    },
                    Spec = new V1PodSpec
                    {
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
                                Name = "gateway",
                                Image = settings.Image,
                                Args = ["gateway"],
                                Env =
                                [
                                    new V1EnvVar { Name = "POLE_ACCESS_BROKER", Value = settings.BrokerUri.AbsoluteUri },
                                    new V1EnvVar { Name = "POLE_ACCESS_TENANT", Value = identity.TenantId },
                                    new V1EnvVar { Name = "POLE_ACCESS_TARGET", Value = identity.DeploymentTargetId },
                                    new V1EnvVar { Name = "POLE_ACCESS_GATEWAY_ID", Value = identity.GatewayId },
                                    new V1EnvVar { Name = "POLE_ACCESS_IDENTITY_FILE", Value = IdentityFile }
                                ],
                                VolumeMounts = [new V1VolumeMount { Name = "identity", MountPath = "/run/pole-access", ReadOnlyProperty = true }],
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
                                Name = "identity",
                                Secret = new V1SecretVolumeSource
                                {
                                    SecretName = GatewayName,
                                    DefaultMode = 288 // 0440; readable through fsGroup.
                                }
                            }
                        ]
                    }
                }
            }
        };
    }

    public static V1Secret BuildIdentitySecret(
        PrivateAccessGatewayIdentity identity,
        byte[] identityPfx) => new()
        {
            ApiVersion = "v1",
            Kind = "Secret",
            Type = "Opaque",
            Metadata = new V1ObjectMeta
            {
                Name = GatewayName,
                NamespaceProperty = Namespace(identity),
                Labels = Labels(identity),
                Annotations = new Dictionary<string, string>
                {
                    [GenerationAnnotation] = identity.Generation.ToString(CultureInfo.InvariantCulture)
                }
            },
            Data = new Dictionary<string, byte[]> { ["identity.pfx"] = identityPfx }
        };

    public static V1NetworkPolicy BuildGatewayEgress(
        PrivateAccessGatewayIdentity identity,
        IReadOnlyList<PrivateAccessGatewayEndpoint> endpoints,
        PrivateAccessGatewaySettings settings)
    {
        if (settings.BrokerAddresses.Count == 0 || settings.BrokerUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Gateway broker egress requires an HTTPS origin and explicit addresses.",
                nameof(settings));
        var rules = endpoints.Select(endpoint => new V1NetworkPolicyEgressRule
        {
            To = [DestinationPeer(identity, endpoint)],
            Ports = [Port(endpoint.TargetPort.Value)]
        }).ToList();
        rules.Add(new V1NetworkPolicyEgressRule
        {
            To = settings.BrokerAddresses.Select(address => new V1NetworkPolicyPeer
            {
                IpBlock = new V1IPBlock
                {
                    Cidr = $"{address}/{(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128)}"
                }
            }).ToList(),
            Ports = [Port(settings.BrokerUri.Port)]
        });
        rules.Add(new V1NetworkPolicyEgressRule
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
        });
        return new V1NetworkPolicy
        {
            ApiVersion = "networking.k8s.io/v1",
            Kind = "NetworkPolicy",
            Metadata = new V1ObjectMeta
            {
                Name = GatewayName,
                NamespaceProperty = Namespace(identity),
                Labels = Labels(identity)
            },
            Spec = new V1NetworkPolicySpec
            {
                PodSelector = Selector(Labels(identity)),
                PolicyTypes = ["Ingress", "Egress"],
                Ingress = [],
                Egress = rules
            }
        };
    }

    public static V1NetworkPolicy BuildDestinationIngress(
        PrivateAccessGatewayIdentity identity,
        PrivateAccessGatewayEndpoint endpoint) => new()
        {
            ApiVersion = "networking.k8s.io/v1",
            Kind = "NetworkPolicy",
            Metadata = new V1ObjectMeta
            {
                Name = DestinationPolicyName(endpoint),
                NamespaceProperty = endpoint.KubernetesNamespace,
                Labels = DestinationPolicyLabels(identity, endpoint)
            },
            Spec = new V1NetworkPolicySpec
            {
                PodSelector = DestinationSelector(identity, endpoint),
                PolicyTypes = ["Ingress"],
                Ingress =
                [
                    new V1NetworkPolicyIngressRule
                    {
                        Ports = [Port(endpoint.TargetPort.Value)],
                        FromProperty =
                        [
                            new V1NetworkPolicyPeer
                            {
                                NamespaceSelector = Selector(new Dictionary<string, string>
                                {
                                    ["kubernetes.io/metadata.name"] = Namespace(identity),
                                    [TenantLabel] = identity.TenantId,
                                    [TargetLabel] = identity.DeploymentTargetId,
                                    [NamespaceLabel] = "true"
                                }),
                                PodSelector = Selector(Labels(identity))
                            }
                        ]
                    }
                ]
            }
        };

    public static string DestinationPolicyName(PrivateAccessGatewayEndpoint endpoint) =>
        $"pole-access-gateway-{Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{endpoint.ResourceDeploymentId}:{endpoint.EndpointId}")))[..24].ToLowerInvariant()}";

    public static Dictionary<string, string> DestinationPolicyLabels(
        PrivateAccessGatewayIdentity identity,
        PrivateAccessGatewayEndpoint endpoint) => new()
        {
            [TenantLabel] = identity.TenantId,
            [TargetLabel] = identity.DeploymentTargetId,
            [GatewayLabel] = identity.GatewayId,
            [RuntimeCellLabel] = endpoint.RuntimeCellId,
            [DeploymentLabel] = endpoint.ResourceDeploymentId
        };

    private static V1NetworkPolicyPeer DestinationPeer(
        PrivateAccessGatewayIdentity identity,
        PrivateAccessGatewayEndpoint endpoint) => new()
        {
            NamespaceSelector = Selector(new Dictionary<string, string>
            {
                ["kubernetes.io/metadata.name"] = endpoint.KubernetesNamespace,
                [TenantLabel] = identity.TenantId,
                [TargetLabel] = identity.DeploymentTargetId,
                [RuntimeCellLabel] = endpoint.RuntimeCellId
            }),
            PodSelector = DestinationSelector(identity, endpoint)
        };

    private static V1LabelSelector DestinationSelector(
        PrivateAccessGatewayIdentity identity,
        PrivateAccessGatewayEndpoint endpoint) => Selector(new Dictionary<string, string>
        {
            [TenantLabel] = identity.TenantId,
            [RuntimeCellLabel] = endpoint.RuntimeCellId,
            [DeploymentLabel] = endpoint.ResourceDeploymentId
        });

    private static V1LabelSelector Selector(Dictionary<string, string> labels) => new() { MatchLabels = labels };
    private static V1NetworkPolicyPort Port(int port) => new() { Protocol = "TCP", Port = port };
}
