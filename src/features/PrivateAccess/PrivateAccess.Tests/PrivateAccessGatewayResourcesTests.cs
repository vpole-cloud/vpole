using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using k8s;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Runtime;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.PrivateAccess.Tests;

public sealed class PrivateAccessGatewayResourcesTests
{
    [Fact]
    public async Task ReconcileAfterRegistrationCrash_ReusesInstalledIdentitySecret()
    {
        var identity = new PrivateAccessGatewayIdentity(
            "0123456789abcdef01234567", "89abcdef0123456701234567",
            "fedcba9876543210fedcba98", 1, 1);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=pole-access-gateway", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(7));
        var pfx = certificate.Export(X509ContentType.Pkcs12);
        var kube = new StoredKubernetes();
        using var client = new Kubernetes(new KubernetesClientConfiguration
        {
            Host = "http://kubernetes.test"
        }, kube);
        var resources = new PrivateAccessGatewayResources(
            new FixedKubernetesProvider(client),
            (id, _) => Task.FromResult<LookupDeploymentTargetOutcome>(Target(id)),
            (_, _, _, _, _) => Task.FromResult<EnsureRuntimeCellIngressBaselineOutcome>(
                new RuntimeCellIngressBaselineReady()));
        var settings = new PrivateAccessGatewaySettings("pole/gateway:1",
            new Uri("https://broker.example:9444"), [IPAddress.Parse("192.0.2.8")]);

        var first = await resources.EnsureAsync(identity, [], settings, pfx, CancellationToken.None);
        var retry = await resources.EnsureAsync(identity, [], settings, null, CancellationToken.None);

        var firstReady = Assert.IsType<PrivateAccessGatewayReady>(first.Value);
        var retryReady = Assert.IsType<PrivateAccessGatewayReady>(retry.Value);
        Assert.Equal(firstReady.CertificateSha256Pin, retryReady.CertificateSha256Pin);
        Assert.Equal(1, kube.SecretCreates);
        Assert.Equal(1, kube.DeploymentCreates);
        Assert.Equal(["networkpolicy", "networkpolicy", "secret", "deployment"], kube.CreatedAfterNamespace);

        Assert.IsType<PrivateAccessGatewayCleanupPending>(
            (await resources.DeleteAsync(identity, [], CancellationToken.None)).Value);
        Assert.IsType<PrivateAccessGatewayDeleted>(
            (await resources.DeleteAsync(identity, [], CancellationToken.None)).Value);
    }

    [Fact]
    public async Task TwoDeploymentsWithSameProviderEndpoint_ProvisionDistinctPolicies()
    {
        var identity = new PrivateAccessGatewayIdentity(
            "0123456789abcdef01234567", "89abcdef0123456701234567",
            "fedcba9876543210fedcba98", 1, 1);
        var endpoints = new[]
        {
            new PrivateAccessGatewayEndpoint("redis", "cell-a", "deployment-a",
                "runtime-a", "redis-a", NetworkPort.Create(6379), NetworkPort.Create(6379)),
            new PrivateAccessGatewayEndpoint("redis", "cell-b", "deployment-b",
                "runtime-b", "redis-b", NetworkPort.Create(6379), NetworkPort.Create(6379))
        };
        var kube = new StoredKubernetes();
        using var client = new Kubernetes(new KubernetesClientConfiguration
        {
            Host = "http://kubernetes.test"
        }, kube);
        var resources = new PrivateAccessGatewayResources(
            new FixedKubernetesProvider(client),
            (id, _) => Task.FromResult<LookupDeploymentTargetOutcome>(Target(id)),
            (_, _, _, _, _) => Task.FromResult<EnsureRuntimeCellIngressBaselineOutcome>(
                new RuntimeCellIngressBaselineReady()));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=pole-access-gateway", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(7));

        var result = await resources.EnsureAsync(identity, endpoints,
            new PrivateAccessGatewaySettings("pole/gateway:1",
                new Uri("https://broker.example:9444"), [IPAddress.Parse("192.0.2.8")]),
            certificate.Export(X509ContentType.Pkcs12), CancellationToken.None);

        Assert.IsType<PrivateAccessGatewayReady>(result.Value);
        Assert.Equal(4, kube.CreatedAfterNamespace.Count(item => item == "networkpolicy"));
    }

    [Fact]
    public async Task ExistingNamespaceFromAnotherTenant_IsNeverAdopted()
    {
        var identity = new PrivateAccessGatewayIdentity(
            "0123456789abcdef01234567", "89abcdef0123456701234567",
            "fedcba9876543210fedcba98", 1, 1);
        var kube = new ForeignNamespaceKubernetes();
        using var client = new Kubernetes(new KubernetesClientConfiguration
        {
            Host = "http://kubernetes.test"
        }, kube);
        var resources = new PrivateAccessGatewayResources(
            new FixedKubernetesProvider(client),
            (id, _) => Task.FromResult<LookupDeploymentTargetOutcome>(Target(id)),
            (_, _, _, _, _) => Task.FromResult<EnsureRuntimeCellIngressBaselineOutcome>(
                new RuntimeCellIngressBaselineReady()));

        var outcome = await resources.EnsureAsync(identity, [],
            new PrivateAccessGatewaySettings("pole/gateway:1", new Uri("https://broker.example:9444"),
                [IPAddress.Parse("192.0.2.8")]), null, CancellationToken.None);

        Assert.IsType<PrivateAccessGatewayOwnershipConflict>(outcome.Value);
        Assert.Equal(0, kube.PolicyWrites);
        Assert.Equal(0, kube.SecretWrites);
        Assert.Equal(0, kube.DeploymentWrites);
    }

    private static DeploymentTargetLookupDto Target(string id) => new(
        id, "Target", "cluster-a", "Kubernetes", 1,
        new DeploymentTargetWorkloadConfigurationLookupDto(1, null,
            new DeploymentTargetNetworkSourcesDto(
                ImmutableDictionary<string, DeploymentTargetNetworkSourceDto>.Empty)),
        1, true, true, false, "Healthy")
    {
        NetworkPolicy = new DeploymentTargetNetworkPolicyDto(
            NetworkPolicyEnforcementStatus.Supported,
            NetworkPolicyReadiness.EnforcementAttested,
            NetworkPolicyReason.OperatorAttestedSupported,
            DateTime.UtcNow,
            NetworkPolicyEvidenceSource.OperatorAttestation,
            false)
    };

    private sealed class FixedKubernetesProvider(IKubernetes client) : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(client);

        public Task<IKubernetes> GetForCleanupAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) =>
            GetAsync(deploymentTargetId, cancellationToken);

        public Task<IKubernetes> GetDefaultAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("Target-specific client required.");
    }

    private sealed class ForeignNamespaceKubernetes : DelegatingHandler
    {
        public int PolicyWrites { get; private set; }
        public int SecretWrites { get; private set; }
        public int DeploymentWrites { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/networkpolicies", StringComparison.Ordinal))
                PolicyWrites++;
            if (path.EndsWith("/secrets", StringComparison.Ordinal))
                SecretWrites++;
            if (path.EndsWith("/deployments", StringComparison.Ordinal))
                DeploymentWrites++;

            if (request.Method == HttpMethod.Post && path == "/api/v1/namespaces")
                return Task.FromResult(Response(request, HttpStatusCode.Conflict, "{}"));
            if (request.Method == HttpMethod.Get && path ==
                "/api/v1/namespaces/pole-access-0123456789abcdef01234567-89abcdef0123456701234567")
                return Task.FromResult(Response(request, HttpStatusCode.OK,
                    """
                    {"metadata":{"name":"pole-access-0123456789abcdef01234567-89abcdef0123456701234567","labels":{"pole.io/tenant-id":"fedcba9876543210fedcba98","pole.io/deployment-target-id":"89abcdef0123456701234567","pole.io/private-access-gateway":"true","pod-security.kubernetes.io/enforce":"restricted"}}}
                    """));
            throw new InvalidOperationException($"Unexpected Kubernetes call {request.Method} {path}.");
        }

        private static HttpResponseMessage Response(HttpRequestMessage request, HttpStatusCode status, string body) =>
            new(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }

    private sealed class StoredKubernetes : DelegatingHandler
    {
        private readonly Dictionary<string, JsonNode> objects = new();
        private readonly List<string> createdAfterNamespace = [];
        public int SecretCreates { get; private set; }
        public int DeploymentCreates { get; private set; }
        public IReadOnlyList<string> CreatedAfterNamespace => createdAfterNamespace;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                var name = body["metadata"]!["name"]!.GetValue<string>();
                var key = $"{path}/{name}";
                if (objects.ContainsKey(key))
                    return Response(request, HttpStatusCode.Conflict, "{}");
                body["metadata"]!["uid"] = "uid-owned";
                body["metadata"]!["resourceVersion"] = "1";
                objects[key] = body;
                if (path.EndsWith("/secrets", StringComparison.Ordinal))
                    SecretCreates++;
                if (path.EndsWith("/deployments", StringComparison.Ordinal))
                    DeploymentCreates++;
                if (path != "/api/v1/namespaces")
                    createdAfterNamespace.Add(path.EndsWith("/networkpolicies", StringComparison.Ordinal)
                        ? "networkpolicy" : path.EndsWith("/secrets", StringComparison.Ordinal)
                            ? "secret" : "deployment");
                return Response(request, HttpStatusCode.Created, body.ToJsonString());
            }
            if (request.Method == HttpMethod.Get)
                return objects.TryGetValue(path, out var existing)
                    ? Response(request, HttpStatusCode.OK, existing.ToJsonString())
                    : Response(request, HttpStatusCode.NotFound, "{}");
            if (request.Method == HttpMethod.Put)
            {
                if (!objects.ContainsKey(path))
                    return Response(request, HttpStatusCode.NotFound, "{}");
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                body["metadata"]!["uid"] = "uid-owned";
                body["metadata"]!["resourceVersion"] = "2";
                objects[path] = body;
                return Response(request, HttpStatusCode.OK, body.ToJsonString());
            }
            if (request.Method == HttpMethod.Delete)
                return objects.Remove(path)
                    ? Response(request, HttpStatusCode.OK, "{}")
                    : Response(request, HttpStatusCode.NotFound, "{}");
            throw new InvalidOperationException($"Unexpected Kubernetes call {request.Method} {path}.");
        }

        private static HttpResponseMessage Response(HttpRequestMessage request, HttpStatusCode status, string body) =>
            new(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }
}
