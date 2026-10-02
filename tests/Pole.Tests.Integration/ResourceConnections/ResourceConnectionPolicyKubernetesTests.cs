using k8s;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceConnections.Application;
using Pole.Features.ResourceConnections.Application.Kubernetes;
using Pole.Features.ResourceConnections.Application.Persistence;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceGroups.Application.Kubernetes;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;
using Pole.Testing;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.ResourceConnections;

[Trait("Layer", "Kubernetes")]
[Trait("Concern", "Feature")]
[Trait("Scope", "SingleCluster")]
[Trait("Feature", "ResourceConnections")]
public sealed class ResourceConnectionPolicyKubernetesTests : KubernetesIntegrationTestBase
{
    private readonly KubernetesClusterFixture clusterFixture;

    public ResourceConnectionPolicyKubernetesTests(KubernetesClusterFixture fixture) : base(fixture)
    {
        clusterFixture = fixture;
    }

    [KubernetesFact]
    public async Task DirectedConnection_UsesPodTargetPortAndExactSourceIdentity_AndRevokesTraffic()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = timeout.Token;
        await using var sourceNamespace = new PeerNamespace(clusterFixture);
        await sourceNamespace.InitializeAsync();
        var destinationCell = await LabelCellAsync(CellId, NamespaceName, ct);
        var sourceCell = await LabelCellAsync(SourceCellId, sourceNamespace.Name, ct);
        var sourceLabels = Labels(SourceCellId, SourceDeploymentId);
        var sourceAddress = await CreatePodAsync("source", sourceLabels, 8888, ct, sourceNamespace.Name);
        await CreatePodAsync("other-source", Labels(SourceCellId, ResourceId), null, ct, sourceNamespace.Name);
        await CreatePodAsync("spoofed-source", sourceLabels, null, ct);
        var destinationAddress = await CreatePodAsync("destination", Labels(CellId, DeploymentId), 8080, ct);
        const string serviceName = "receiver-service";
        await Kubernetes.CoreV1.CreateNamespacedServiceAsync(new V1Service
        {
            Metadata = new V1ObjectMeta { Name = serviceName },
            Spec = new V1ServiceSpec
            {
                Selector = Labels(CellId, DeploymentId),
                Ports = [new V1ServicePort { Name = "http", Port = 80, TargetPort = 8080, Protocol = "TCP" }]
            }
        }, NamespaceName, cancellationToken: ct);
        var host = $"{serviceName}.{NamespaceName}.svc.cluster.local";
        await WaitForTrafficAsync("source", host, 80, true, ct, sourceNamespace.Name);
        await WaitForTrafficAsync("other-source", host, 80, true, ct, sourceNamespace.Name);
        await WaitForTrafficAsync("spoofed-source", host, 80, true, ct);
        await WaitForTrafficAsync("source", destinationAddress, 9090, true, ct, sourceNamespace.Name);
        await WaitForTrafficAsync("destination", sourceAddress, 8888, true, ct);

        var clients = new FixedClientProvider(Kubernetes);
        var baseline = new KubernetesRuntimeCellNamespaceManager(clients);
        Assert.IsType<Unit>((await baseline.EnsureCreatedAsync(destinationCell, ct)).Value);
        Assert.IsType<Unit>((await baseline.EnsureCreatedAsync(sourceCell, ct)).Value);
        await WaitForTrafficAsync("source", host, 80, false, ct, sourceNamespace.Name);

        var snapshot = ConnectionTargetSnapshot.Create(
            TargetId, DeploymentId, CellId, NamespaceName, serviceName, 80, 8080,
            [new ConnectionSourcePlacement(SourceDeploymentId, SourceCellId, sourceNamespace.Name)]);
        var current = ResourceConnectionFactory.Create(
            ConnectionId, TenantId, "source-to-receiver",
            ResourceTypeId.Create("pole/web-service"), SourceDeploymentId,
            ResourceTypeId.Create("pole/web-service"), ResourceId, "http", [snapshot]);
        var registrations = new ServiceCollection();
        registrations.AddSingleton<IKubernetesClientProvider>(clients);
        registrations.AddSingleton<EnsureRuntimeCellIngressBaseline>(async (_, _, _, _, token) =>
        {
            Assert.IsType<Unit>((await baseline.EnsureCreatedAsync(destinationCell, token)).Value);
            return new RuntimeCellIngressBaselineReady();
        });
        registrations.AddSingleton<LoadResourceConnection>((tenantId, connectionId, _) =>
            Task.FromResult<ResourceConnection?>(
                tenantId == current.TenantId && connectionId == current.Id ? current : null));
        registrations.AddSingleton<LoadResourceConnectionTombstone>((tenantId, connectionId, _) =>
            Task.FromResult<ResourceConnectionCleanupTombstone?>(null));
        registrations.AddResourceConnectionsFeature();
        await using var services = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IResourceConnectionPolicyTarget>();

        Assert.IsType<ResourceConnectionPolicySucceeded>((await writer.ApplyAsync(current, snapshot, ct)).Value);
        await WaitForTrafficAsync("source", host, 80, true, ct, sourceNamespace.Name);
        await WaitForTrafficAsync("other-source", host, 80, false, ct, sourceNamespace.Name);
        await WaitForTrafficAsync("spoofed-source", host, 80, false, ct);
        await WaitForTrafficAsync("source", destinationAddress, 9090, false, ct, sourceNamespace.Name);
        await WaitForTrafficAsync("destination", sourceAddress, 8888, false, ct);
        await WaitForTrafficAsync("source", host, 80, true, ct, sourceNamespace.Name);

        current = current.BeginDeletion(DateTime.UtcNow);
        Assert.IsType<ResourceConnectionPolicySucceeded>((await writer.DeleteAsync(current, snapshot, ct)).Value);
        await WaitForTrafficAsync("source", host, 80, false, ct, sourceNamespace.Name);
    }

    private async Task<RuntimeCell> LabelCellAsync(string id, string namespaceName, CancellationToken ct)
    {
        var cell = RuntimeCellFactory.Create(
            TenantId, GroupId, ScopeId, TargetId, namespaceName,
            new RuntimeCellFactory.CreateOptions(id, DateTime.UtcNow));
        var existing = await Kubernetes.CoreV1.ReadNamespaceAsync(namespaceName, cancellationToken: ct);
        foreach (var label in RuntimeCellNamespaceManifestBuilder.Build(cell).Metadata.Labels)
            existing.Metadata.Labels[label.Key] = label.Value;
        await Kubernetes.CoreV1.ReplaceNamespaceAsync(existing, namespaceName, cancellationToken: ct);
        return cell;
    }

    private static Dictionary<string, string> Labels(string cellId, string deploymentId) => new()
    {
        ["pole.io/tenant-id"] = TenantId,
        ["pole.io/runtime-cell-id"] = cellId,
        ["pole.io/resource-deployment-id"] = deploymentId
    };

    private async Task<string> CreatePodAsync(
        string name,
        Dictionary<string, string> labels,
        int? port,
        CancellationToken ct,
        string? kubernetesNamespace = null)
    {
        var podNamespace = kubernetesNamespace ?? NamespaceName;
        var command = port is { } number
            ? $"mkdir -p /www; echo pole-network-test >/www/index.html; httpd -p 9090 -h /www; exec httpd -f -p {number} -h /www"
            : "exec sleep 600";
        await Kubernetes.CoreV1.CreateNamespacedPodAsync(new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = name, Labels = labels },
            Spec = new V1PodSpec
            {
                RestartPolicy = "Never",
                Containers = [new V1Container
                {
                    Name = "probe",
                    Image = "busybox:1.37.0",
                    Command = ["sh", "-c", command],
                    Resources = new V1ResourceRequirements
                    {
                        Requests = new Dictionary<string, ResourceQuantity>
                        {
                            ["cpu"] = new("10m"), ["memory"] = new("8Mi")
                        },
                        Limits = new Dictionary<string, ResourceQuantity>
                        {
                            ["cpu"] = new("100m"), ["memory"] = new("32Mi")
                        }
                    }
                }]
            }
        }, podNamespace, cancellationToken: ct);
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (DateTime.UtcNow < deadline)
        {
            var pod = await Kubernetes.CoreV1.ReadNamespacedPodAsync(name, podNamespace, cancellationToken: ct);
            if (pod.Status?.PodIP is { Length: > 0 } ip &&
                pod.Status.ContainerStatuses?.All(container => container.Ready) == true)
                return ip;
            await Task.Delay(500, ct);
        }
        throw await CreateTimeoutExceptionAsync($"Pod {name} did not become ready.");
    }

    private async Task WaitForTrafficAsync(
        string client,
        string address,
        int port,
        bool allowed,
        CancellationToken ct,
        string? kubernetesNamespace = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        var consecutive = 0;
        while (DateTime.UtcNow < deadline)
        {
            // Exec transport failures propagate; only wget's exit status measures traffic.
            var exitCode = await Kubernetes.NamespacedPodExecAsync(
                client, kubernetesNamespace ?? NamespaceName, "probe",
                ["wget", "-q", "-T", "2", "-O", "/dev/null", $"http://{address}:{port}"],
                false,
                (_, stdout, stderr) => Task.WhenAll(
                    stdout.CopyToAsync(Stream.Null, ct), stderr.CopyToAsync(Stream.Null, ct)),
                ct);
            consecutive = (exitCode == 0) == allowed ? consecutive + 1 : 0;
            if (consecutive >= (allowed ? 1 : 3))
                return;
            await Task.Delay(300, ct);
        }
        throw await CreateTimeoutExceptionAsync(
            $"Expected traffic {client} -> {address}:{port} allowed={allowed}.");
    }

    private sealed class PeerNamespace(KubernetesClusterFixture peerFixture)
        : KubernetesIntegrationTestBase(peerFixture), IAsyncDisposable
    {
        public string Name => NamespaceName;

        ValueTask IAsyncDisposable.DisposeAsync() => new(base.DisposeAsync());
    }

    private sealed class FixedClientProvider(IKubernetes kubernetes) : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) => Task.FromResult(kubernetes);

        public Task<IKubernetes> GetForCleanupAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) =>
            GetAsync(deploymentTargetId, cancellationToken);

        public Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A persisted target is required.");
    }

    private const string TenantId = "507f1f77bcf86cd799439011";
    private const string SourceCellId = "507f1f77bcf86cd799439018";
    private const string SourceDeploymentId = "507f1f77bcf86cd799439019";
    private const string ConnectionId = "507f1f77bcf86cd79943901a";
    private const string ResourceId = "507f1f77bcf86cd799439012";
    private const string GroupId = "507f1f77bcf86cd799439013";
    private const string ScopeId = "507f1f77bcf86cd799439014";
    private const string CellId = "507f1f77bcf86cd799439015";
    private const string TargetId = "507f1f77bcf86cd799439016";
    private const string DeploymentId = "507f1f77bcf86cd799439017";
}
