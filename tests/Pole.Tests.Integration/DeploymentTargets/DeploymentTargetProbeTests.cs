using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Validation;
using Pole.Features.DeploymentTargets.Domain;
using Xunit;

namespace Pole.Tests.Integration.DeploymentTargets;

public sealed class DeploymentTargetProbeTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProbeAsync_UsesNamespaceIdentityAndVerifiesEveryRequiredPermission()
    {
        var handler = new KubernetesProbeHandler();
        using var kubernetes = CreateClient(handler);
        var probe = CreateProbe(ObservedAt);

        var observation = await probe.InvokeAsync(kubernetes, connectionVersion: 7);

        Assert.True(
            observation.IsUsable,
            $"{observation.Health.FailureCode}: {observation.Health.FailureMessage}; " +
            $"requests={string.Join(",", handler.RequestedPaths)}");
        Assert.Equal("kubernetes:kube-system:pole-cluster-uid", observation.ClusterIdentity);
        Assert.Equal(DeploymentTargetHealthStatus.Healthy, observation.Health.Status);
        Assert.Equal(ObservedAt.UtcDateTime, observation.Health.LastCheckedAtUtc);
        Assert.Equal(ObservedAt.UtcDateTime, observation.Health.LastHealthyAtUtc);
        Assert.Equal(7, observation.Health.ConnectionVersion);
        Assert.True(observation.Capabilities.IsKnown);
        Assert.Equal("v1.31.4", observation.Capabilities.KubernetesVersion);
        Assert.Equal(ObservedAt.UtcDateTime, observation.Capabilities.ObservedAtUtc);
        Assert.Equal(["get", "create", "update", "patch", "delete"], handler.ReviewedVerbs);
        Assert.Equal(
            new DeploymentTargetNamespaceCapabilities(true, true, true, true, true),
            observation.Capabilities.NamespaceCapabilities);
        Assert.Equal(
            new DeploymentTargetNetworkPolicyCapabilities(true, true, true, true, true),
            observation.Capabilities.NetworkPolicyCapabilities);
        Assert.Equal(["get", "create", "update", "patch", "delete"], handler.ReviewedNetworkPolicyVerbs);
    }

    [Fact]
    public async Task ProbeAsync_MarksMissingPermissionAsDegraded_AndPreservesMonotonicHealthTime()
    {
        var handler = new KubernetesProbeHandler(deniedVerb: "delete");
        using var kubernetes = CreateClient(handler);
        var probe = CreateProbe(ObservedAt);
        var lastHealthyAt = ObservedAt.AddMinutes(5).UtcDateTime;

        var observation = await probe.InvokeAsync(kubernetes, connectionVersion: 3, lastHealthyAtUtc: lastHealthyAt);

        Assert.False(observation.IsUsable);
        Assert.Equal("kubernetes:kube-system:pole-cluster-uid", observation.ClusterIdentity);
        Assert.Equal(DeploymentTargetHealthStatus.Degraded, observation.Health.Status);
        Assert.Equal("InsufficientNamespacePermissions", observation.Health.FailureCode);
        Assert.Equal(lastHealthyAt, observation.Health.LastCheckedAtUtc);
        Assert.Equal(lastHealthyAt, observation.Health.LastHealthyAtUtc);
        Assert.True(observation.Capabilities.IsKnown);
        Assert.False(observation.Capabilities.NamespaceCapabilities.CanDelete);
        Assert.True(observation.Capabilities.NamespaceCapabilities.CanPatch);
    }

    [Fact]
    public async Task ProbeAsync_MissingNetworkPolicyPermission_DegradesTargetReadiness()
    {
        var handler = new KubernetesProbeHandler(deniedVerb: "create", deniedResource: "networkpolicies");
        using var kubernetes = CreateClient(handler);

        var observation = await CreateProbe(ObservedAt).InvokeAsync(kubernetes, connectionVersion: 7);

        Assert.False(observation.IsUsable);
        Assert.Equal("InsufficientNetworkPolicyPermissions", observation.Health.FailureCode);
        Assert.True(observation.Capabilities.NamespaceCapabilities.CanCreate);
        Assert.False(observation.Capabilities.NetworkPolicyCapabilities.CanCreate);
        Assert.True(observation.Capabilities.NetworkPolicyCapabilities.CanGet);
    }

    private static DeploymentTargetProbe CreateProbe(DateTimeOffset utcNow) => new(
        new FixedTimeProvider(utcNow),
        new DeploymentTargetOptions { ValidationTimeoutSeconds = 5 });

    private static Kubernetes CreateClient(DelegatingHandler handler)
    {
        var configuration = new KubernetesClientConfiguration
        {
            Host = "https://probe.pole.test:6443",
            SkipTlsVerify = true,
        };
        return new Kubernetes(configuration, handler);
    }

    private sealed class FixedTimeProvider(
        DateTimeOffset utcNow
    ) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class KubernetesProbeHandler(
        string? deniedVerb = null,
        string? deniedResource = null
    ) : DelegatingHandler
    {
        public List<string> ReviewedVerbs { get; } = [];
        public List<string> ReviewedNetworkPolicyVerbs { get; } = [];
        public List<string> RequestedPaths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestedPath = request.RequestUri?.AbsolutePath;
            RequestedPaths.Add(requestedPath ?? "<null>");
            var path = requestedPath?.TrimEnd('/');
            var response = path switch
            {
                "/version" => Json(
                    """
                    {
                      "major": "1",
                      "minor": "31",
                      "gitVersion": "v1.31.4",
                      "gitCommit": "test",
                      "gitTreeState": "clean",
                      "buildDate": "2026-07-22T00:00:00Z",
                      "goVersion": "go1.24",
                      "compiler": "gc",
                      "platform": "linux/amd64"
                    }
                    """),
                "/api/v1/namespaces/kube-system" => Json(
                    """
                    {
                      "apiVersion": "v1",
                      "kind": "Namespace",
                      "metadata": {
                        "name": "kube-system",
                        "uid": "pole-cluster-uid"
                      }
                    }
                    """),
                "/apis/authorization.k8s.io/v1/selfsubjectaccessreviews" => await ReviewAsync(
                    request,
                    cancellationToken),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            response.RequestMessage = request;
            return response;
        }

        private async Task<HttpResponseMessage> ReviewAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(request.Content);
            await using var stream = await request.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var verb = document.RootElement
                .GetProperty("spec")
                .GetProperty("resourceAttributes")
                .GetProperty("verb")
                .GetString();
            var resource = document.RootElement
                .GetProperty("spec")
                .GetProperty("resourceAttributes")
                .GetProperty("resource")
                .GetString();
            var group = document.RootElement
                .GetProperty("spec")
                .GetProperty("resourceAttributes")
                .GetProperty("group")
                .GetString();
            Assert.False(string.IsNullOrWhiteSpace(verb));
            if (resource == "namespaces")
            {
                Assert.Equal("", group);
                ReviewedVerbs.Add(verb);
            }
            else
            {
                Assert.Equal("networkpolicies", resource);
                Assert.Equal("networking.k8s.io", group);
                ReviewedNetworkPolicyVerbs.Add(verb);
            }

            var allowed = !string.Equals(verb, deniedVerb, StringComparison.Ordinal) || !string.Equals(
                resource,
                deniedResource ?? "namespaces",
                StringComparison.Ordinal);
            return Json(
                $$"""
                  {
                    "apiVersion": "authorization.k8s.io/v1",
                    "kind": "SelfSubjectAccessReview",
                    "status": {
                      "allowed": {{allowed.ToString().ToLowerInvariant()}}
                    }
                  }
                  """);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
