using System.Net;
using k8s;
using k8s.Models;
using Pole.Features.DeploymentTargets.Contracts;
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
    private readonly KubernetesClusterFixture cluster;

    public PrivateAccessIsolationKubernetesTests(KubernetesClusterFixture fixture) : base(fixture)
    {
        cluster = fixture;
    }
    [KubernetesFact]
    public async Task EndpointWorker_CannotReachItsNeighbourOrAnotherPort_AndOtherWorkersCannotBorrowAccess()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = timeout.Token;
        await using var workerNamespace = new PeerNamespace(cluster);
        await workerNamespace.InitializeAsync();
        var cell = RuntimeCellFactory.Create(TenantId, GroupId, ScopeId, TargetId, NamespaceName,
            new RuntimeCellFactory.CreateOptions(CellId, DateTime.UtcNow));
        await AddNamespaceLabelsAsync(NamespaceName,
            RuntimeCellNamespaceManifestBuilder.Build(cell).Metadata.Labels, ct);
        await AddNamespaceLabelsAsync(workerNamespace.Name, new Dictionary<string, string>
        {
            [PrivateAccessWorkerManifest.TargetLabel] = TargetId,
            [PrivateAccessWorkerManifest.WorkerNamespaceLabel] = "true"
        }, ct);
        var a = await PodAsync("database-a", NamespaceName, WorkloadLabels(DeploymentA), true, ct);
        var b = await PodAsync("database-b", NamespaceName, WorkloadLabels(DeploymentB), true, ct);
        await PodAsync("worker-a", workerNamespace.Name, WorkerLabels(SessionA), false, ct);
        await PodAsync("worker-b", workerNamespace.Name, WorkerLabels(SessionB), false, ct);
        // Positive controls establish reachability before testing any denial.
        await TrafficAsync("worker-a", workerNamespace.Name, a, 8080, true, ct);
        await TrafficAsync("worker-a", workerNamespace.Name, b, 8080, true, ct);
        await TrafficAsync("worker-a", workerNamespace.Name, a, 9090, true, ct);
        await TrafficAsync("worker-b", workerNamespace.Name, a, 8080, true, ct);
        var baseline = new KubernetesRuntimeCellNamespaceManager(new FixedClient(Kubernetes));
        var baselineResult = await baseline.EnsureCreatedAsync(cell, ct);
        Assert.IsType<Pole.ResultType.Unit>(baselineResult.Value);
        await TrafficAsync("worker-a", workerNamespace.Name, a, 8080, false, ct);

        var destination = new PrivateAccessWorkerDestination(TenantId, TargetId, CellId,
            DeploymentA, NamespaceName, "database-a", NetworkPort.Create(8080), NetworkPort.Create(8080));
        var settings = new PrivateAccessWorkerSettings(workerNamespace.Name, "unused:test",
            new Uri("https://broker.example"), [IPAddress.Parse("192.0.2.8")]);
        await Kubernetes.NetworkingV1.CreateNamespacedNetworkPolicyAsync(
            PrivateAccessWorkerManifest.BuildWorkerBaseline(TargetId, workerNamespace.Name),
            workerNamespace.Name, cancellationToken: ct);
        await Kubernetes.NetworkingV1.CreateNamespacedNetworkPolicyAsync(
            PrivateAccessWorkerManifest.BuildWorkerPolicy(SessionA, destination, settings,
                DateTimeOffset.UtcNow.AddMinutes(5)), workerNamespace.Name, cancellationToken: ct);
        await Kubernetes.NetworkingV1.CreateNamespacedNetworkPolicyAsync(
            PrivateAccessWorkerManifest.BuildDestinationPolicy(SessionA, destination, settings,
                DateTimeOffset.UtcNow.AddMinutes(5)), NamespaceName, cancellationToken: ct);
        await TrafficAsync("worker-a", workerNamespace.Name, a, 8080, true, ct);
        await TrafficAsync("worker-a", workerNamespace.Name, b, 8080, false, ct);
        await TrafficAsync("worker-a", workerNamespace.Name, a, 9090, false, ct);
        await TrafficAsync("worker-b", workerNamespace.Name, a, 8080, false, ct);
        // Keep destination ingress open while removing the session egress rule:
        // a still-running/terminating worker must fall back to namespace default-deny.
        await Kubernetes.NetworkingV1.DeleteNamespacedNetworkPolicyAsync(
            PrivateAccessWorkerManifest.Name(SessionA), workerNamespace.Name, cancellationToken: ct);
        await TrafficAsync("worker-a", workerNamespace.Name, a, 8080, false, ct);
        await Kubernetes.NetworkingV1.DeleteNamespacedNetworkPolicyAsync(
            PrivateAccessWorkerManifest.Name(SessionA), NamespaceName, cancellationToken: ct);
        await TrafficAsync("worker-a", workerNamespace.Name, a, 8080, false, ct);
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
                Containers =
                [
                    new V1Container
                    {
                        Name = "probe",
                        Image = "busybox:1.37.0",
                        Command = ["sh", "-c", server
                            ? "mkdir -p /www; echo test >/www/index.html; httpd -p 9090 -h /www; exec httpd -f -p 8080 -h /www"
                            : "exec sleep 600"],
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

    private static Dictionary<string, string> WorkloadLabels(string deployment) => new()
    {
        [PrivateAccessWorkerManifest.TenantLabel] = TenantId,
        [PrivateAccessWorkerManifest.RuntimeCellLabel] = CellId,
        [PrivateAccessWorkerManifest.DeploymentLabel] = deployment
    };

    private static Dictionary<string, string> WorkerLabels(string session) => new()
    {
        [PrivateAccessWorkerManifest.TenantLabel] = TenantId,
        [PrivateAccessWorkerManifest.TargetLabel] = TargetId,
        [PrivateAccessWorkerManifest.SessionLabel] = session
    };

    private sealed class PeerNamespace(KubernetesClusterFixture cluster)
        : KubernetesIntegrationTestBase(cluster), IAsyncDisposable
    {
        public string Name => NamespaceName;
        ValueTask IAsyncDisposable.DisposeAsync() => new(base.DisposeAsync());
    }

    private sealed class FixedClient(IKubernetes client) : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(string deploymentTargetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(client);
        public Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Private Access requires an exact deployment target.");
    }

    private const string TenantId = "507f1f77bcf86cd799439011";
    private const string GroupId = "507f1f77bcf86cd799439013";
    private const string ScopeId = "507f1f77bcf86cd799439014";
    private const string CellId = "507f1f77bcf86cd799439015";
    private const string TargetId = "507f1f77bcf86cd799439016";
    private const string DeploymentA = "507f1f77bcf86cd799439017";
    private const string DeploymentB = "507f1f77bcf86cd799439018";
    private const string SessionA = "507f1f77bcf86cd799439019";
    private const string SessionB = "507f1f77bcf86cd79943901a";
}
