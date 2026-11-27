// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using k8s;
using k8s.Models;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Application.Kubernetes;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Application.Kubernetes;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;
using Pole.Testing;
using Pole.Tests.Kubernetes.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Kubernetes.ResourceDeployments;

[Trait("Layer", "Kubernetes")]
[Trait("Concern", "Feature")]
[Trait("Scope", "SingleCluster")]
[Trait("Feature", "ResourceDeployments")]
public sealed class ResourceIngressPolicyKubernetesTests : KubernetesIntegrationTestBase
{
    private const string TenantId = "507f1f77bcf86cd799439011";
    private const string ResourceId = "507f1f77bcf86cd799439012";
    private const string GroupId = "507f1f77bcf86cd799439013";
    private const string ScopeId = "507f1f77bcf86cd799439014";
    private const string CellId = "507f1f77bcf86cd799439015";
    private const string TargetId = "507f1f77bcf86cd799439016";
    private const string DeploymentId = "507f1f77bcf86cd799439017";
    private readonly KubernetesClusterFixture clusterFixture;

    public ResourceIngressPolicyKubernetesTests(KubernetesClusterFixture fixture) : base(fixture) =>
        clusterFixture = fixture;

    [KubernetesFact]
    public async Task BaselineAndPublicPolicies_EnforceSelectedSourcesAndPorts_AndRevokeTraffic()
    {
        using var timing = Timings.Measure("kubernetes.policy.scenario");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = timeout.Token;
        var peer = new PeerNamespace(clusterFixture, Timings);
        await using var peerCleanup = Timings.MeasureDisposal(peer, "kubernetes.peer.dispose");
        await peer.InitializeAsync();
        await CreatePodAsync(
            "spoofed",
            new Dictionary<string, string> { ["test-source"] = "selected" },
            null,
            ct,
            peer.Name);
        var cell = RuntimeCellFactory.Create(
            TenantId,
            GroupId,
            ScopeId,
            TargetId,
            NamespaceName,
            new RuntimeCellFactory.CreateOptions(CellId, DateTime.UtcNow));
        var existing = await Kubernetes.CoreV1.ReadNamespaceAsync(NamespaceName, cancellationToken: ct);
        foreach (var label in RuntimeCellNamespaceManifestBuilder.Build(cell).Metadata.Labels)
            existing.Metadata.Labels[label.Key] = label.Value;
        await Kubernetes.CoreV1.ReplaceNamespaceAsync(existing, NamespaceName, cancellationToken: ct);

        await CreatePodAsync("selected", new Dictionary<string, string> { ["test-source"] = "selected" }, null, ct);
        await CreatePodAsync("unmatched", new Dictionary<string, string> { ["test-source"] = "unmatched" }, null, ct);
        var app = await CreatePodAsync(
            "application",
            new Dictionary<string, string>
            {
                ["pole.io/tenant-id"] = TenantId,
                ["pole.io/runtime-cell-id"] = CellId,
                ["pole.io/resource-deployment-id"] = DeploymentId,
            },
            8080,
            ct);
        var solver = await CreatePodAsync(
            "solver",
            new Dictionary<string, string>
            {
                ["acme.cert-manager.io/http01-solver"] = "true",
            },
            8089,
            ct);
        var unrelated = await CreatePodAsync("unrelated", new Dictionary<string, string>(), 8089, ct);

        // Establish real positive controls before introducing any deny policy.
        foreach (var client in new[] { "selected", "unmatched" })
        {
            await WaitForTrafficAsync(client, app, 8080, allowed: true, ct);
            await WaitForTrafficAsync(client, solver, 8089, allowed: true, ct);
            await WaitForTrafficAsync(client, unrelated, 8089, allowed: true, ct);
        }

        await WaitForTrafficAsync("spoofed", app, 8080, allowed: true, ct, peer.Name);

        var provider = new FixedClientProvider(Kubernetes);
        var baseline = new KubernetesRuntimeCellNamespaceManager(provider);
        Assert.IsType<Unit>((await baseline.EnsureCreatedAsync(cell, ct)).Value);
        await WaitForTrafficAsync("selected", app, 8080, allowed: false, ct);
        await WaitForTrafficAsync("unmatched", app, 8080, allowed: false, ct);

        var deployment = Deployment();
        var source = new DeploymentTargetPodNetworkSourceDto(
            NamespaceName,
            ImmutableDictionary<string, string>.Empty.Add("test-source", "selected"));
        var target = TargetConfiguration(source);
        var writer = new KubernetesResourceIngressPolicyManager(
            (_, _) => Task.FromResult<ResourceDeployment?>(deployment),
            (_, _) => Task.FromResult<LookupDeploymentTargetOutcome>(target),
            async (request, cancellationToken) =>
            {
                Assert.IsType<Unit>((await baseline.EnsureCreatedAsync(cell, cancellationToken)).Value);
                return new RuntimeCellIngressBaselineReady();
            },
            provider,
            Catalog());
        var appRequest = Request("web-public", [8080]);
        var solverRequest = Request("web-http01", [8089]);
        Assert.IsType<ResourceIngressPolicyApplied>((await writer.InvokeAsync(appRequest, ct)).Value);
        Assert.IsType<ResourceIngressPolicyApplied>((await writer.InvokeAsync(solverRequest, ct)).Value);

        await WaitForTrafficAsync("selected", app, 8080, allowed: true, ct);
        await WaitForTrafficAsync("selected", solver, 8089, allowed: true, ct);
        await WaitForTrafficAsync("unmatched", app, 8080, allowed: false, ct);
        await WaitForTrafficAsync("unmatched", solver, 8089, allowed: false, ct);
        await WaitForTrafficAsync("selected", unrelated, 8089, allowed: false, ct);
        await WaitForTrafficAsync("spoofed", app, 8080, allowed: false, ct, peer.Name);
        await WaitForTrafficAsync("spoofed", solver, 8089, allowed: false, ct, peer.Name);
        // A still-working server/source proves negative probes were not caused by failed pods.
        await WaitForTrafficAsync("selected", app, 8080, allowed: true, ct);

        Assert.IsType<ResourceIngressPolicyApplied>(
            (await writer.InvokeAsync(appRequest with { AllowedTcpPorts = [] }, ct)).Value);
        Assert.IsType<ResourceIngressPolicyApplied>(
            (await writer.InvokeAsync(solverRequest with { AllowedTcpPorts = [] }, ct)).Value);
        await WaitForTrafficAsync("selected", app, 8080, allowed: false, ct);
        await WaitForTrafficAsync("selected", solver, 8089, allowed: false, ct);

        // Reapplying the authoritative desired state restores traffic after revocation.
        Assert.IsType<ResourceIngressPolicyApplied>((await writer.InvokeAsync(appRequest, ct)).Value);
        await WaitForTrafficAsync("selected", app, 8080, allowed: true, ct);
    }

    private async Task<string> CreatePodAsync(
        string name,
        Dictionary<string, string> labels,
        int? port,
        CancellationToken ct,
        string? kubernetesNamespace = null)
    {
        using var timing = Timings.Measure("kubernetes.pod.create-and-ready");
        var podNamespace = kubernetesNamespace ?? NamespaceName;
        var command = port is { } number
            ? $"mkdir -p /www; echo pole-network-test >/www/index.html; exec httpd -f -p {number} -h /www"
            : "exec sleep 600";
        await Kubernetes.CoreV1.CreateNamespacedPodAsync(
            new V1Pod
            {
                Metadata = new V1ObjectMeta { Name = name, Labels = labels },
                Spec = new V1PodSpec
                {
                    RestartPolicy = "Never",
                    Containers =
                    [
                        new V1Container
                        {
                            Name = "probe",
                            Image = "busybox:1.37.0",
                            Command = ["sh", "-c", command],
                            Resources = new V1ResourceRequirements
                            {
                                Requests = new Dictionary<string, ResourceQuantity>
                                {
                                    ["cpu"] = new("10m"), ["memory"] = new("8Mi"),
                                },
                                Limits = new Dictionary<string, ResourceQuantity>
                                {
                                    ["cpu"] = new("100m"), ["memory"] = new("32Mi"),
                                },
                            },
                        },
                    ],
                },
            },
            podNamespace,
            cancellationToken: ct);
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
        using var timing = Timings.Measure(allowed ? "kubernetes.traffic.allow" : "kubernetes.traffic.deny");
        var deadline = DateTime.UtcNow.AddSeconds(25);
        var consecutive = 0;
        while (DateTime.UtcNow < deadline)
        {
            // Exec transport failures propagate; only wget's exit status measures traffic.
            var exitCode = await Kubernetes.NamespacedPodExecAsync(
                client,
                kubernetesNamespace ?? NamespaceName,
                "probe",
                ["wget", "-q", "-T", "2", "-O", "/dev/null", $"http://{address}:{port}"],
                false,
                (
                    _, stdout,
                    stderr) => Task.WhenAll(stdout.CopyToAsync(Stream.Null, ct), stderr.CopyToAsync(Stream.Null, ct)),
                ct);
            consecutive = exitCode == 0 == allowed ? consecutive + 1 : 0;
            if (consecutive >= (allowed ? 1 : 3))
                return;
            await Task.Delay(300, ct);
        }

        throw await CreateTimeoutExceptionAsync($"Expected traffic {client} -> {address}:{port} allowed={allowed}.");
    }

    private static ResourceIngressPolicyCatalog Catalog() => new([Definition("web-public"), Definition("web-http01")]);

    private static ResourceIngressPolicyDefinition Definition(string purpose) => new(
        ResourceTypeId.Create("pole/web-service"),
        purpose,
        RequiresIngressPlacement: true,
        ports => ports.Count == 1,
        request => purpose == "web-http01"
            ? ImmutableDictionary<string, string>.Empty.Add("acme.cert-manager.io/http01-solver", "true")
            : ImmutableDictionary<string, string>.Empty
                .Add("pole.io/tenant-id", request.TenantId)
                .Add("pole.io/runtime-cell-id", request.RuntimeCellId)
                .Add("pole.io/resource-deployment-id", request.ResourceDeploymentId),
        (_, sources) => sources.Get("public-ingress"));

    private ResourceIngressPolicyRequest Request(string purpose, ImmutableList<int> ports) => new(
        TenantId,
        ResourceTypeId.Create("pole/web-service"),
        ResourceId,
        DeploymentId,
        CellId,
        TargetId,
        NamespaceName,
        purpose,
        1,
        1,
        ports.Select(NetworkPort.Create).ToImmutableList());

    private ResourceDeployment Deployment() => ResourceDeploymentFactory.Create(
        TenantId,
        ResourceTypeId.Create("pole/web-service"),
        ResourceId,
        GroupId,
        ScopeId,
        ResolvedResourcePlacement.Create(
            CellId,
            TargetId,
            NamespaceName,
            1,
            1,
            ResolvedIngressConfiguration.Create("traefik", "issuer", "example.test", ["web.example.test"]),
            null,
            KubernetesComputeTestData.Compute(),
            DateTime.UtcNow),
        ResourcePlacementCapability.SingleTarget,
        true,
        ResourceDeploymentFactory.DefaultOptions with
        {
            Id = DeploymentId,
            CreatedAtUtc = DateTime.UtcNow,
        });

    private static DeploymentTargetLookupDto TargetConfiguration(DeploymentTargetNetworkSourceDto source) => new(
        TargetId,
        "Target",
        "cluster",
        "InCluster",
        1,
        new DeploymentTargetWorkloadConfigurationLookupDto(
            1,
            new DeploymentTargetIngressConfigurationLookupDto("traefik", "issuer", "example.test"),
            new DeploymentTargetNetworkSourcesDto(
                ImmutableDictionary<string, DeploymentTargetNetworkSourceDto>.Empty.Add("public-ingress", source))),
        1,
        true,
        true,
        false,
        "Healthy",
        TechnicalName: "test-target",
        NetworkPolicy: new DeploymentTargetNetworkPolicyDto(
            NetworkPolicyEnforcementStatus.Supported,
            NetworkPolicyReadiness.EnforcementAttested,
            NetworkPolicyReason.OperatorAttestedSupported,
            DateTime.UtcNow,
            NetworkPolicyEvidenceSource.OperatorAttestation,
            false));

    private sealed class PeerNamespace(
        KubernetesClusterFixture peerFixture,
        TestTimings timings
    ) : KubernetesIntegrationTestBase(peerFixture, timings), IAsyncDisposable
    {
        public string Name => NamespaceName;

        ValueTask IAsyncDisposable.DisposeAsync() => new(base.DisposeAsync());
    }

    private sealed class FixedClientProvider(
        IKubernetes kubernetes
    ) : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(string deploymentTargetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(kubernetes);

        public Task<IKubernetes> GetForCleanupAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) => GetAsync(deploymentTargetId, cancellationToken);

        public Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A persisted target is required.");
    }
}
