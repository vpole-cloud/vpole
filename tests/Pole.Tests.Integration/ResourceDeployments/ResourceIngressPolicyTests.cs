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
    public void Manifest_UsesStableDestinationAndSingleConjunctivePodSourcePeer()
    {
        var request = Request(ResourceIngressPolicyPurpose.WebServicePublic, [8080]);
        var source = PodSource();

        var policy = ResourceIngressPolicyManifestBuilder.Build(request, source);

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
        var policy = ResourceIngressPolicyManifestBuilder.Build(
            Request(ResourceIngressPolicyPurpose.FunctionGateway, [8080]),
            new DeploymentTargetCidrNetworkSourceDto("192.0.2.9/32"));

        var peer = Assert.Single(Assert.Single(policy.Spec.Ingress).FromProperty);
        Assert.Equal("192.0.2.9/32", peer.IpBlock.Cidr);
        Assert.Null(peer.NamespaceSelector);
        Assert.Null(peer.PodSelector);
    }

    [Fact]
    public void Http01Manifest_SelectsOnlySolverPods_AndTombstoneHasNoAllowRule()
    {
        var request = Request(ResourceIngressPolicyPurpose.WebServiceHttp01, [8089]);
        var policy = ResourceIngressPolicyManifestBuilder.Build(request, PodSource());

        Assert.Equal(
            new Dictionary<string, string> { ["acme.cert-manager.io/http01-solver"] = "true" },
            policy.Spec.PodSelector.MatchLabels);
        Assert.Equal("8089", Assert.Single(Assert.Single(policy.Spec.Ingress).Ports).Port.Value);

        var tombstone = ResourceIngressPolicyManifestBuilder.Build(
            request with { AllowedTcpPorts = [] },
            PodSource());
        Assert.Empty(tombstone.Spec.Ingress);
    }

    [Fact]
    public async Task OlderPlacementRevision_ReappliesCurrentSourceWithoutClosingIngress()
    {
        var older = Request(ResourceIngressPolicyPurpose.WebServicePublic, [8080]);
        var existing = ResourceIngressPolicyManifestBuilder.Build(
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
            new FixedClientProvider(kubernetes));

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
        var request = Request(ResourceIngressPolicyPurpose.WebServicePublic, [8080]);
        var existing = ResourceIngressPolicyManifestBuilder.Build(request, PodSource());
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
            new FixedClientProvider(kubernetes));

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
        var request = Request(ResourceIngressPolicyPurpose.WebServicePublic, [8080]);
        var foreign = ResourceIngressPolicyManifestBuilder.Build(request, PodSource());
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
            new FixedClientProvider(kubernetes));

        var result = await subject.InvokeAsync(request);

        Assert.IsType<ResourceIngressPolicyOwnershipConflict>(result.Value);
        Assert.Equal(0, handler.ReplaceRequests);
    }

    [Fact]
    public async Task DeleteGeneration_ClosesIngressAndTreatsMissingNamespaceAsClean()
    {
        var deployment = Deployment().RequestDeletion(
            new DateTime(2026, 9, 27, 10, 1, 0, DateTimeKind.Utc));
        var request = Request(ResourceIngressPolicyPurpose.WebServicePublic, []) with
        {
            DeploymentDesiredGeneration = deployment.DesiredGeneration
        };
        using var handler = new PolicyApiHandler(
            ResourceIngressPolicyManifestBuilder.Build(request, null),
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
            new FixedClientProvider(kubernetes));

        var result = await subject.InvokeAsync(request);

        Assert.IsType<ResourceIngressPolicyApplied>(result.Value);
        Assert.Equal(1, handler.CreateRequests);
        Assert.Equal(0, handler.ReadRequests);
    }

    private static ResourceIngressPolicyRequest Request(
        ResourceIngressPolicyPurpose purpose,
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
            ports);

    private static DeploymentTargetPodNetworkSourceDto PodSource(string name = "traefik") => new(
        "kube-system",
        ImmutableDictionary<string, string>.Empty.Add("app.kubernetes.io/name", name));

    private static ResourceDeployment Deployment()
    {
        var now = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);
        return ResourceDeploymentFactory.Create(
            TenantId,
            ResourceTypeId.Create("pole/web-service"),
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
            new DeploymentTargetNetworkSourcesDto(source, null, null)),
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
