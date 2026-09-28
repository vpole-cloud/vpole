using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using k8s.Models;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Application.Kubernetes;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

public sealed class ResourceIngressPolicyTests
{
    [Fact]
    public void Catalog_RejectsDuplicateAndInvalidPurposeContributions()
    {
        var definition = Definition("web-public");
        Assert.Throws<InvalidOperationException>(() =>
            new ResourceIngressPolicyCatalog([definition, definition]));
        Assert.Throws<InvalidOperationException>(() =>
            new ResourceIngressPolicyCatalog([definition with { Purpose = "-invalid" }]));
    }

    [Fact]
    public void Manifest_RejectsProviderSourceWithInvalidNamespace()
    {
        var source = new DeploymentTargetPodNetworkSourceDto(
            "Kube_System",
            ImmutableDictionary<string, string>.Empty.Add("app", "gateway"));

        Assert.Throws<ArgumentException>(() => Manifest(Request("web-public", [8080]), source));
    }

    [Fact]
    public async Task DefaultNetworkPort_IsRejectedBeforeAnyInfrastructureAccess()
    {
        var loadCalled = false;
        var subject = new KubernetesResourceIngressPolicyManager(
            (_, _) =>
            {
                loadCalled = true;
                throw new InvalidOperationException("Invalid ports must not load deployment state.");
            },
            (_, _) => throw new InvalidOperationException("Invalid ports must not load a target."),
            (_, _, _, _, _) => throw new InvalidOperationException("Invalid ports must not check baseline."),
            null!,
            Catalog());

        await Assert.ThrowsAsync<ArgumentException>(() => subject.InvokeAsync(
            Request("web-public", [8080]) with { AllowedTcpPorts = [default(NetworkPort)] }));
        Assert.False(loadCalled);
    }

    [Fact]
    public void Manifest_UsesStableDestinationAndSingleConjunctivePodSourcePeer()
    {
        var request = Request("web-public", [8080]);
        var source = PodSource();

        var policy = Manifest(request, source);

        Assert.Equal($"pole-web-public-{DeploymentId}", policy.Metadata.Name);
        Assert.Equal(Namespace, policy.Metadata.NamespaceProperty);
        Assert.Equal(TenantId, policy.Spec.PodSelector.MatchLabels["pole.io/tenant-id"]);
        Assert.Equal(RuntimeCellId, policy.Spec.PodSelector.MatchLabels["pole.io/runtime-cell-id"]);
        Assert.Equal(DeploymentId, policy.Spec.PodSelector.MatchLabels["pole.io/resource-deployment-id"]);
        Assert.Equal(["Ingress"], policy.Spec.PolicyTypes);
        Assert.Null(policy.Spec.Egress);
        var rule = Assert.Single(policy.Spec.Ingress);
        var peer = Assert.Single(rule.FromProperty);
        Assert.Equal("kube-system", peer.NamespaceSelector.MatchLabels["kubernetes.io/metadata.name"]);
        Assert.Equal("traefik", peer.PodSelector.MatchLabels["app.kubernetes.io/name"]);
        Assert.Null(peer.IpBlock);
        Assert.Equal("8080", Assert.Single(rule.Ports).Port.Value);
    }

    [Fact]
    public void Manifest_UsesNarrowCidrWithoutPodOrNamespaceSelector()
    {
        var policy = Manifest(
            Request("function-gateway", [8080]),
            new DeploymentTargetCidrNetworkSourceDto("192.0.2.9/32"));

        var peer = Assert.Single(Assert.Single(policy.Spec.Ingress).FromProperty);
        Assert.Equal("192.0.2.9/32", peer.IpBlock.Cidr);
        Assert.Null(peer.NamespaceSelector);
        Assert.Null(peer.PodSelector);
    }

    [Fact]
    public void Http01Manifest_SelectsOnlySolverPods_AndTombstoneHasNoAllowRule()
    {
        var request = Request("web-http01", [8089]);
        var policy = Manifest(request, PodSource());

        Assert.Equal(
            new Dictionary<string, string> { ["acme.cert-manager.io/http01-solver"] = "true" },
            policy.Spec.PodSelector.MatchLabels);
        Assert.Equal("8089", Assert.Single(Assert.Single(policy.Spec.Ingress).Ports).Port.Value);

        var tombstone = Manifest(
            request with { AllowedTcpPorts = [] },
            PodSource());
        Assert.Empty(tombstone.Spec.Ingress);
    }

    [Fact]
    public async Task OlderPlacementRevision_ReappliesCurrentSourceWithoutClosingIngress()
    {
        var older = Request("web-public", [8080]);
        var existing = Manifest(
            older with { TargetWorkloadConfigurationRevision = 2 },
            PodSource("traefik-new"));
        existing.Metadata.ResourceVersion = "7";
        using var handler = new PolicyApiHandler(existing);
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);
        var subject = new KubernetesResourceIngressPolicyManager(
            (request, ct) => Task.FromResult<ResourceDeployment?>(Deployment()),
            (id, ct) => Task.FromResult<LookupDeploymentTargetOutcome>(
                Target(revision: 2, source: PodSource("traefik-new"))),
            (tenantId, cellId, targetId, kubernetesNamespace, ct) =>
                Task.FromResult<EnsureRuntimeCellIngressBaselineOutcome>(
                    new RuntimeCellIngressBaselineReady()),
            new FixedClientProvider(kubernetes), Catalog());

        var result = await subject.InvokeAsync(older);

        Assert.IsType<ResourceIngressPolicyApplied>(result.Value);
        Assert.Equal(1, handler.CreateRequests);
        Assert.Equal(1, handler.ReadRequests);
        Assert.Equal(1, handler.ReplaceRequests);
        Assert.Equal("2", handler.ReplacedPolicy!.Metadata.Annotations[
            ResourceIngressPolicyManifestBuilder.TargetConfigurationRevisionAnnotation]);
        Assert.Equal("traefik-new", Assert.Single(Assert.Single(
            handler.ReplacedPolicy.Spec.Ingress).FromProperty)
            .PodSelector.MatchLabels["app.kubernetes.io/name"]);
    }

    [Fact]
    public async Task MissingCurrentSource_ClosesOldAllowBeforeReportingUnavailable()
    {
        var request = Request("web-public", [8080]);
        var existing = Manifest(request, PodSource());
        existing.Metadata.ResourceVersion = "7";
        using var handler = new PolicyApiHandler(existing);
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);
        var baselineCalled = false;
        var subject = new KubernetesResourceIngressPolicyManager(
            (value, ct) => Task.FromResult<ResourceDeployment?>(Deployment()),
            (id, ct) => Task.FromResult<LookupDeploymentTargetOutcome>(
                Target(revision: 2, source: null)),
            (tenantId, cellId, targetId, kubernetesNamespace, ct) =>
            {
                baselineCalled = true;
                return Task.FromResult<EnsureRuntimeCellIngressBaselineOutcome>(
                    new RuntimeCellIngressBaselineReady());
            },
            new FixedClientProvider(kubernetes), Catalog());

        var result = await subject.InvokeAsync(request);

        Assert.IsType<ResourceIngressPolicySourceUnavailable>(result.Value);
        Assert.False(baselineCalled);
        Assert.Empty(handler.ReplacedPolicy!.Spec.Ingress);
        Assert.Equal("2", handler.ReplacedPolicy.Metadata.Annotations[
            ResourceIngressPolicyManifestBuilder.TargetConfigurationRevisionAnnotation]);
    }

    [Fact]
    public async Task ForeignPolicyWithReservedName_IsNeverReplaced()
    {
        var request = Request("web-public", [8080]);
        var foreign = Manifest(request, PodSource());
        foreign.Metadata.ResourceVersion = "7";
        foreign.Metadata.Labels["pole.io/resource-deployment-id"] = "another-deployment";
        using var handler = new PolicyApiHandler(foreign);
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);
        var subject = new KubernetesResourceIngressPolicyManager(
            (value, ct) => Task.FromResult<ResourceDeployment?>(Deployment()),
            (id, ct) => Task.FromResult<LookupDeploymentTargetOutcome>(
                Target(revision: 1, source: PodSource())),
            (tenantId, cellId, targetId, kubernetesNamespace, ct) =>
                Task.FromResult<EnsureRuntimeCellIngressBaselineOutcome>(
                    new RuntimeCellIngressBaselineReady()),
            new FixedClientProvider(kubernetes), Catalog());

        var result = await subject.InvokeAsync(request);

        Assert.IsType<ResourceIngressPolicyOwnershipConflict>(result.Value);
        Assert.Equal(0, handler.ReplaceRequests);
    }

    [Fact]
    public async Task DeleteGeneration_ClosesIngressAndTreatsMissingNamespaceAsClean()
    {
        var deployment = Deployment().RequestDeletion(
            new DateTime(2026, 9, 27, 10, 1, 0, DateTimeKind.Utc));
        var request = Request("web-public", []) with
        {
            DeploymentDesiredGeneration = deployment.DesiredGeneration
        };
        using var handler = new PolicyApiHandler(
            Manifest(request, null),
            HttpStatusCode.NotFound);
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);
        var subject = new KubernetesResourceIngressPolicyManager(
            (value, ct) => Task.FromResult<ResourceDeployment?>(deployment),
            (id, ct) => Task.FromResult<LookupDeploymentTargetOutcome>(
                Target(revision: 1, source: null)),
            (tenantId, cellId, targetId, kubernetesNamespace, ct) =>
                throw new InvalidOperationException("Cleanup does not require a Ready cell."),
            new FixedClientProvider(kubernetes), Catalog());

        var result = await subject.InvokeAsync(request);

        Assert.IsType<ResourceIngressPolicyApplied>(result.Value);
        Assert.Equal(1, handler.CreateRequests);
        Assert.Equal(0, handler.ReadRequests);
    }

    [Fact]
    public async Task ExternalProviderContribution_RevokesMissingCurrentSourceAtNewTargetRevision()
    {
        var type = ResourceTypeId.Create("vendor/metrics");
        var request = Request("metrics-scrape", [9000]) with { ResourceType = type };
        var definition = new ResourceIngressPolicyDefinition(
            type,
            request.Purpose,
            RequiresIngressPlacement: false,
            ports => ports.SequenceEqual([NetworkPort.Create(9000)]),
            value => ImmutableDictionary<string, string>.Empty
                .Add("pole.io/resource-deployment-id", value.ResourceDeploymentId)
                .Add("metrics.example/role", "collector"),
            (_, sources) => sources.Get("vendor/metrics"));
        var existing = ResourceIngressPolicyManifestBuilder.Build(
            request,
            new ResourceIngressPolicyCidrSource("192.0.2.0/28"),
            definition);
        existing.Metadata.ResourceVersion = "7";
        using var handler = new PolicyApiHandler(existing);
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" }, handler);
        var subject = new KubernetesResourceIngressPolicyManager(
            (_, _) => Task.FromResult<ResourceDeployment?>(Deployment(type)),
            (_, _) => Task.FromResult<LookupDeploymentTargetOutcome>(Target(2, null)),
            (_, _, _, _, _) => throw new InvalidOperationException("Revocation needs no baseline."),
            new FixedClientProvider(kubernetes),
            new ResourceIngressPolicyCatalog([definition]));

        var result = await subject.InvokeAsync(request);

        Assert.IsType<ResourceIngressPolicySourceUnavailable>(result.Value);
        Assert.Equal($"pole-metrics-scrape-{DeploymentId}", handler.ReplacedPolicy!.Metadata.Name);
        Assert.Equal("collector", handler.ReplacedPolicy.Spec.PodSelector.MatchLabels["metrics.example/role"]);
        Assert.Empty(handler.ReplacedPolicy.Spec.Ingress);
        Assert.Equal("2", handler.ReplacedPolicy.Metadata.Annotations[
            ResourceIngressPolicyManifestBuilder.TargetConfigurationRevisionAnnotation]);
    }

    private static V1NetworkPolicy Manifest(
        ResourceIngressPolicyRequest request,
        DeploymentTargetNetworkSourceDto? source) =>
        ResourceIngressPolicyManifestBuilder.Build(
            request,
            source switch
            {
                DeploymentTargetPodNetworkSourceDto pod => new ResourceIngressPolicyPodSource(
                    pod.Namespace, pod.PodLabels),
                DeploymentTargetCidrNetworkSourceDto cidr => new ResourceIngressPolicyCidrSource(cidr.Cidr),
                null => null
            },
            Definition(request.Purpose));

    private static ResourceIngressPolicyCatalog Catalog() => new(
        [Definition("web-public"), Definition("web-http01"), Definition("function-gateway")]);

    private static ResourceIngressPolicyDefinition Definition(string purpose) => new(
        ResourceTypeId.Create("pole/web-service"),
        purpose,
        purpose is "web-public" or "web-http01",
        ports => ports.Count > 0,
        request => purpose == "web-http01"
            ? ImmutableDictionary<string, string>.Empty.Add("acme.cert-manager.io/http01-solver", "true")
            : ImmutableDictionary<string, string>.Empty
                .Add("pole.io/tenant-id", request.TenantId)
                .Add("pole.io/runtime-cell-id", request.RuntimeCellId)
                .Add("pole.io/resource-deployment-id", request.ResourceDeploymentId),
        (_, sources) => sources.Get("public-ingress"));

    private static ResourceIngressPolicyRequest Request(
        string purpose,
        ImmutableList<int> ports) => new(
            TenantId,
            ResourceTypeId.Create("pole/web-service"),
            ResourceId,
            DeploymentId,
            RuntimeCellId,
            TargetId,
            Namespace,
            purpose,
            DeploymentDesiredGeneration: 1,
            TargetWorkloadConfigurationRevision: 1,
            ports.Select(NetworkPort.Create).ToImmutableList());

    private static DeploymentTargetPodNetworkSourceDto PodSource(string name = "traefik") => new(
        "kube-system",
        ImmutableDictionary<string, string>.Empty.Add("app.kubernetes.io/name", name));

    private static ResourceDeployment Deployment(ResourceTypeId? resourceType = null)
    {
        var now = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);
        return ResourceDeploymentFactory.Create(
            TenantId,
            resourceType ?? ResourceTypeId.Create("pole/web-service"),
            ResourceId,
            ResourceGroupId,
            ScopeId,
            ResolvedResourcePlacement.Create(
                RuntimeCellId,
                TargetId,
                Namespace,
                runtimeCellGeneration: 1,
                targetWorkloadConfigurationRevision: 1,
                ResolvedIngressConfiguration.Create(
                    "traefik",
                    "issuer",
                    "example.test",
                    ["web.example.test"]),
                storage: null,
                ResourceDeploymentTestData.Compute(),
                now),
            ResourcePlacementCapability.SingleTarget,
            runtimeCellReady: true,
            new ResourceDeploymentFactory.CreateOptions(DeploymentId, now));
    }

    private static DeploymentTargetLookupDto Target(
        long revision,
        DeploymentTargetNetworkSourceDto? source) => new(
        TargetId,
        "Target",
        "cluster",
        "InCluster",
        1,
        new DeploymentTargetWorkloadConfigurationLookupDto(
            revision,
            new DeploymentTargetIngressConfigurationLookupDto("traefik", "issuer", "example.test"),
            new DeploymentTargetNetworkSourcesDto(source is null
                ? ImmutableDictionary<string, DeploymentTargetNetworkSourceDto>.Empty
                : ImmutableDictionary<string, DeploymentTargetNetworkSourceDto>.Empty.Add("public-ingress", source))),
        1,
        Enabled: true,
        AcceptsWorkloads: true,
        DeletionRequested: false,
        HealthStatus: "Healthy")
        {
            NetworkPolicy = new DeploymentTargetNetworkPolicyDto(
            NetworkPolicyEnforcementStatus.Supported,
            NetworkPolicyReadiness.EnforcementAttested,
            NetworkPolicyReason.OperatorAttestedSupported,
            DateTime.UtcNow,
            NetworkPolicyEvidenceSource.OperatorAttestation,
            false)
        };

    private const string TenantId = "507f1f77bcf86cd799439011";
    private const string ResourceId = "507f1f77bcf86cd799439012";
    private const string ResourceGroupId = "507f1f77bcf86cd799439013";
    private const string ScopeId = "507f1f77bcf86cd799439014";
    private const string RuntimeCellId = "507f1f77bcf86cd799439015";
    private const string TargetId = "507f1f77bcf86cd799439016";
    private const string DeploymentId = "507f1f77bcf86cd799439017";
    private const string Namespace = "pole-ingress-test";

    private sealed class FixedClientProvider(IKubernetes kubernetes) : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) => Task.FromResult(kubernetes);

        public Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A persisted target is required.");
    }

    private sealed class PolicyApiHandler(
        V1NetworkPolicy existing,
        HttpStatusCode createStatus = HttpStatusCode.Conflict) : DelegatingHandler
    {
        public int CreateRequests { get; private set; }
        public int ReadRequests { get; private set; }
        public int ReplaceRequests { get; private set; }
        public V1NetworkPolicy? ReplacedPolicy { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body;
            HttpStatusCode status;
            if (request.Method == HttpMethod.Post)
            {
                CreateRequests++;
                status = createStatus;
                body = Status(status);
            }
            else if (request.Method == HttpMethod.Get)
            {
                ReadRequests++;
                status = HttpStatusCode.OK;
                body = JsonSerializer.Serialize(new
                {
                    apiVersion = existing.ApiVersion,
                    kind = existing.Kind,
                    metadata = new
                    {
                        name = existing.Metadata.Name,
                        @namespace = existing.Metadata.NamespaceProperty,
                        labels = existing.Metadata.Labels,
                        annotations = existing.Metadata.Annotations,
                        resourceVersion = existing.Metadata.ResourceVersion
                    },
                    spec = new
                    {
                        podSelector = new { matchLabels = existing.Spec.PodSelector.MatchLabels },
                        policyTypes = existing.Spec.PolicyTypes,
                        ingress = Array.Empty<object>()
                    }
                });
            }
            else
            {
                ReplaceRequests++;
                ReplacedPolicy = JsonSerializer.Deserialize<V1NetworkPolicy>(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                status = HttpStatusCode.OK;
                body = JsonSerializer.Serialize(new
                {
                    apiVersion = existing.ApiVersion,
                    kind = existing.Kind,
                    metadata = new
                    {
                        name = existing.Metadata.Name,
                        @namespace = existing.Metadata.NamespaceProperty,
                        labels = existing.Metadata.Labels,
                        annotations = existing.Metadata.Annotations,
                        resourceVersion = existing.Metadata.ResourceVersion
                    },
                    spec = new
                    {
                        podSelector = new { matchLabels = existing.Spec.PodSelector.MatchLabels },
                        policyTypes = existing.Spec.PolicyTypes,
                        ingress = Array.Empty<object>()
                    }
                });
            }

            return new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        private static string Status(HttpStatusCode status) => JsonSerializer.Serialize(new
        {
            apiVersion = "v1",
            kind = "Status",
            status = "Failure",
            reason = status.ToString(),
            code = (int)status,
            message = "not exposed"
        });
    }
}
