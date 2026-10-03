using System.Net;
using k8s;
using k8s.Models;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Runtime;
using Pole.Features.ResourceGroups.Application.Kubernetes;
using Pole.Features.ResourceGroups.Domain;
using Pole.Testing;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.PrivateAccess;

[Trait("Layer", "Kubernetes")]
[Trait("Concern", "Feature")]
[Trait("Scope", "SingleCluster")]
[Trait("Feature", "PrivateAccess")]
public sealed class PrivateAccessIsolationKubernetesTests : KubernetesIntegrationTestBase
{
    public PrivateAccessIsolationKubernetesTests(KubernetesClusterFixture fixture) : base(fixture)
    {
    }
    [KubernetesFact]
    public async Task Gateway_CannotReachOtherDeploymentOrPort_AndOtherPodsCannotBorrowAccess()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = timeout.Token;
        var tenantId = Guid.NewGuid().ToString("N")[..24];
        var targetId = Guid.NewGuid().ToString("N")[..24];
        var identity = new PrivateAccessGatewayIdentity(
            tenantId, targetId, PrivateAccessGatewayIds.StableId(tenantId, targetId), 1, 1);
        var gatewayNamespace = PrivateAccessGatewayManifest.Namespace(identity);
        var cell = RuntimeCellFactory.Create(tenantId, GroupId, ScopeId, targetId, NamespaceName,
            new RuntimeCellFactory.CreateOptions(CellId, DateTime.UtcNow));
        await AddNamespaceLabelsAsync(NamespaceName,
            RuntimeCellNamespaceManifestBuilder.Build(cell).Metadata.Labels, ct);
        await Kubernetes.CoreV1.CreateNamespaceAsync(
            PrivateAccessGatewayManifest.BuildNamespace(identity), cancellationToken: ct);
        try
        {
            var a = await PodAsync("database-a", NamespaceName,
                WorkloadLabels(tenantId, DeploymentA), true, ct);
            var b = await PodAsync("database-b", NamespaceName,
                WorkloadLabels(tenantId, DeploymentB), true, ct);
            await ServiceAsync("database-a", WorkloadLabels(tenantId, DeploymentA), ct);
            await ServiceAsync("database-b", WorkloadLabels(tenantId, DeploymentB), ct);
            await PodAsync("gateway-a", gatewayNamespace,
                GatewayLabels(identity), false, ct);
            var otherLabels = GatewayLabels(identity);
            otherLabels[PrivateAccessGatewayManifest.GatewayLabel] =
                Guid.NewGuid().ToString("N")[..24];
            await PodAsync("gateway-b", gatewayNamespace, otherLabels, false, ct);

            // Positive controls establish reachability before testing any denial.
            await TrafficAsync("gateway-a", gatewayNamespace, a, 8080, true, ct);
            await TrafficAsync("gateway-a", gatewayNamespace, b, 8080, true, ct);
            await TrafficAsync("gateway-a", gatewayNamespace, a, 9090, true, ct);
            await TrafficAsync("gateway-b", gatewayNamespace, a, 8080, true, ct);
            var baseline = new KubernetesRuntimeCellNamespaceManager(new FixedClient(Kubernetes));
            var baselineResult = await baseline.EnsureCreatedAsync(cell, ct);
            Assert.IsType<Pole.ResultType.Unit>(baselineResult.Value);
            await TrafficAsync("gateway-a", gatewayNamespace, a, 8080, false, ct);

            var endpoint = new PrivateAccessGatewayEndpoint(
                "database", CellId, DeploymentA, NamespaceName, "database-a",
                NetworkPort.Create(8080), NetworkPort.Create(8080));
            var settings = new PrivateAccessGatewaySettings(
                "unused:test", new Uri("https://broker.example:9444"),
                [IPAddress.Parse("192.0.2.8")]);
            await Kubernetes.NetworkingV1.CreateNamespacedNetworkPolicyAsync(
                PrivateAccessGatewayManifest.BuildBaseline(identity),
                gatewayNamespace, cancellationToken: ct);
            await Kubernetes.NetworkingV1.CreateNamespacedNetworkPolicyAsync(
                PrivateAccessGatewayManifest.BuildGatewayEgress(identity, [endpoint], settings),
                gatewayNamespace, cancellationToken: ct);
            await Kubernetes.NetworkingV1.CreateNamespacedNetworkPolicyAsync(
                PrivateAccessGatewayManifest.BuildDestinationIngress(identity, endpoint),
                NamespaceName, cancellationToken: ct);
            await TrafficAsync("gateway-a", gatewayNamespace, a, 8080, true, ct);
            await TrafficAsync("gateway-a", gatewayNamespace,
                $"database-a.{NamespaceName}.svc", 8080, true, ct);
            await TrafficAsync("gateway-a", gatewayNamespace, b, 8080, false, ct);
            await TrafficAsync("gateway-a", gatewayNamespace,
                $"database-b.{NamespaceName}.svc", 8080, false, ct);
            await TrafficAsync("gateway-a", gatewayNamespace, a, 9090, false, ct);
            await TrafficAsync("gateway-b", gatewayNamespace, a, 8080, false, ct);

            // The default-deny baseline still blocks a running pod after egress deletion.
            await Kubernetes.NetworkingV1.DeleteNamespacedNetworkPolicyAsync(
                PrivateAccessGatewayManifest.GatewayName, gatewayNamespace, cancellationToken: ct);
            await TrafficAsync("gateway-a", gatewayNamespace, a, 8080, false, ct);
        }
        finally
        {
            await Kubernetes.CoreV1.DeleteNamespaceAsync(gatewayNamespace,
                cancellationToken: CancellationToken.None);
        }
    }

    private async Task AddNamespaceLabelsAsync(
        string name,
        IDictionary<string, string> labels,
        CancellationToken ct)
    {
        var current = await Kubernetes.CoreV1.ReadNamespaceAsync(name, cancellationToken: ct);
        foreach (var label in labels)
            current.Metadata.Labels[label.Key] = label.Value;
        await Kubernetes.CoreV1.ReplaceNamespaceAsync(current, name, cancellationToken: ct);
    }

    private Task<V1Service> ServiceAsync(
        string name,
        Dictionary<string, string> selector,
        CancellationToken ct) => Kubernetes.CoreV1.CreateNamespacedServiceAsync(
        new V1Service
        {
            Metadata = new V1ObjectMeta { Name = name },
            Spec = new V1ServiceSpec
            {
                Selector = selector,
                Ports = [new V1ServicePort { Port = 8080, Protocol = "TCP" }]
            }
        }, NamespaceName, cancellationToken: ct);

    private async Task<string> PodAsync(
        string name,
        string namespaceName,
        Dictionary<string, string> labels,
        bool server,
        CancellationToken ct)
    {
        await Kubernetes.CoreV1.CreateNamespacedPodAsync(new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = name, Labels = labels },
            Spec = new V1PodSpec
            {
                RestartPolicy = "Never",
                AutomountServiceAccountToken = false,
                SecurityContext = new V1PodSecurityContext
                {
                    RunAsNonRoot = true,
                    RunAsUser = 65532,
                    RunAsGroup = 65532,
                    SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" }
                },
                Containers =
                [
                    new V1Container
                    {
                        Name = "probe",
                        Image = "busybox:1.37.0",
                        Command = ["sh", "-c", server
                            ? "mkdir -p /tmp/www; echo test >/tmp/www/index.html; httpd -p 9090 -h /tmp/www; exec httpd -f -p 8080 -h /tmp/www"
                            : "exec sleep 600"],
                        SecurityContext = new V1SecurityContext
                        {
                            AllowPrivilegeEscalation = false,
                            Capabilities = new V1Capabilities { Drop = ["ALL"] }
                        },
                        Resources = new V1ResourceRequirements
                        {
                            Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("10m"), ["memory"] = new("8Mi") },
                            Limits = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("100m"), ["memory"] = new("32Mi") }
                        }
                    }
                ]
            }
        }, namespaceName, cancellationToken: ct);
        var until = DateTime.UtcNow.AddMinutes(1);
        while (DateTime.UtcNow < until)
        {
            var pod = await Kubernetes.CoreV1.ReadNamespacedPodAsync(name, namespaceName, cancellationToken: ct);
            if (pod.Status?.PodIP is { Length: > 0 } address &&
                pod.Status.ContainerStatuses?.All(container => container.Ready) == true)
                return address;
            await Task.Delay(500, ct);
        }
        throw await CreateTimeoutExceptionAsync($"Private Access pod {name} was not ready.");
    }

    private async Task TrafficAsync(
        string pod,
        string namespaceName,
        string address,
        int port,
        bool allowed,
        CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(25);
        var consecutive = 0;
        while (DateTime.UtcNow < until)
        {
            var exit = await Kubernetes.NamespacedPodExecAsync(pod, namespaceName, "probe",
                ["wget", "-q", "-T", "2", "-O", "/dev/null", $"http://{address}:{port}"], false,
                (_, stdout, stderr) => Task.WhenAll(stdout.CopyToAsync(Stream.Null, ct), stderr.CopyToAsync(Stream.Null, ct)), ct);
            consecutive = (exit == 0) == allowed ? consecutive + 1 : 0;
            if (consecutive >= (allowed ? 1 : 3))
                return;
            await Task.Delay(300, ct);
        }
        throw await CreateTimeoutExceptionAsync($"Private Access traffic {pod} to port {port}: expected allowed={allowed}.");
    }

    private static Dictionary<string, string> WorkloadLabels(
        string tenantId,
        string deployment) => new()
        {
            [PrivateAccessGatewayManifest.TenantLabel] = tenantId,
            [PrivateAccessGatewayManifest.RuntimeCellLabel] = CellId,
            [PrivateAccessGatewayManifest.DeploymentLabel] = deployment
        };

    private static Dictionary<string, string> GatewayLabels(
        PrivateAccessGatewayIdentity identity) =>
        PrivateAccessGatewayManifest.Labels(identity);

    private sealed class FixedClient(IKubernetes client) : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(string deploymentTargetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(client);
        public Task<IKubernetes> GetForCleanupAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) =>
            GetAsync(deploymentTargetId, cancellationToken);

        public Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Private Access requires an exact deployment target.");
    }

    private const string GroupId = "507f1f77bcf86cd799439013";
    private const string ScopeId = "507f1f77bcf86cd799439014";
    private const string CellId = "507f1f77bcf86cd799439015";
    private const string DeploymentA = "507f1f77bcf86cd799439017";
    private const string DeploymentB = "507f1f77bcf86cd799439018";
}
