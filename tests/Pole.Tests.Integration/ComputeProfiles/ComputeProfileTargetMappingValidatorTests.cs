using System.Collections.Immutable;
using System.Net;
using System.Text;
using k8s;
using MongoDB.Bson;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Validation;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Xunit;
using Xunit.Sdk;

namespace Pole.Tests.Integration.ComputeProfiles;

public sealed class ComputeProfileTargetMappingValidatorTests
{
    private const string DeploymentTargetId = "507f1f77bcf86cd799439071";

    [Fact]
    public async Task Mapping_matching_runtime_class_architecture_affinity_and_extended_resource_succeeds()
    {
        var kubernetes = CreateKubernetes(
            new SchedulingApiHandler(runtimeClassExists: true, nodePool: "accelerated", gpuCapacity: 2));
        var validator = new KubernetesComputeProfileTargetMappingValidator(new FixedProvider(kubernetes), AdmitTarget);

        var result = await validator.InvokeAsync(CreateProfile(runtimeClassName: "kata"));

        Assert.IsType<ComputeProfileTargetMappingsValid>(result.Value);
    }

    [Fact]
    public async Task Mapping_without_a_node_satisfying_extended_resources_is_semantically_invalid()
    {
        var kubernetes = CreateKubernetes(
            new SchedulingApiHandler(runtimeClassExists: true, nodePool: "accelerated", gpuCapacity: 0));
        var validator = new KubernetesComputeProfileTargetMappingValidator(new FixedProvider(kubernetes), AdmitTarget);

        var result = await validator.InvokeAsync(CreateProfile(runtimeClassName: "kata"));

        var error = Assert.IsType<ComputeProfileTargetMappingsInvalid>(result.Value);
        Assert.Contains("TargetMappings[0].Scheduling", error.Errors.Keys);
    }

    [Fact]
    public async Task Missing_runtime_class_is_semantically_invalid_without_exposing_provider_response()
    {
        var kubernetes = CreateKubernetes(
            new SchedulingApiHandler(runtimeClassExists: false, nodePool: "accelerated", gpuCapacity: 2));
        var validator = new KubernetesComputeProfileTargetMappingValidator(new FixedProvider(kubernetes), AdmitTarget);

        var result = await validator.InvokeAsync(CreateProfile(runtimeClassName: "kata"));

        var error = Assert.IsType<ComputeProfileTargetMappingsInvalid>(result.Value);
        Assert.Contains("TargetMappings[0].RuntimeClassName", error.Errors.Keys);
        Assert.DoesNotContain(
            "provider-secret",
            error.Errors.Values.SelectMany(messages => messages),
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task Rejected_deployment_target_is_a_local_mapping_error_without_calling_kubernetes()
    {
        var validator = new KubernetesComputeProfileTargetMappingValidator(new ThrowIfUsedProvider(), RejectTarget);

        var result = await validator.InvokeAsync(CreateProfile(runtimeClassName: null));

        var error = Assert.IsType<ComputeProfileTargetMappingsInvalid>(result.Value);
        Assert.Contains("TargetMappings[0].DeploymentTargetId", error.Errors.Keys);
    }

    [Fact]
    public async Task Kubernetes_connection_unavailability_remains_an_infrastructure_failure()
    {
        var validator = new KubernetesComputeProfileTargetMappingValidator(
            new ConnectionUnavailableProvider(),
            AdmitTarget);

        var failure = await Assert.ThrowsAsync<DeploymentTargetUnavailableException>(() =>
            validator.InvokeAsync(CreateProfile(runtimeClassName: null)));

        Assert.Equal(DeploymentTargetUnavailableReason.ConnectionUnavailable, failure.Reason);
    }

    private static ComputeProfile CreateProfile(string? runtimeClassName)
    {
        var required = ComputeProfileNodeAffinityTerm.Create(
        [
            ComputeProfileNodeAffinityExpression.Create(
                "pole.io/node-pool",
                ComputeNodeAffinityOperator.In,
                ["accelerated"]),
        ]);
        return ComputeProfileFactory.Create(
            "test-profile",
            "Accelerated",
            "",
            ComputeProfileResourceRequirements.Create(
                ComputeProfileResourceQuantity.Create(250, 256),
                ComputeProfileResourceQuantity.Create(1000, 1024)),
            ComputeArchitecture.Amd64,
            [ComputeProfileExtendedResource.Create("nvidia.com/gpu", 1)],
            [ResourceTypeId.Create("pole/web-service")],
            AllTenantsComputeProfileAccess.Create(),
            [
                ComputeProfileTargetMapping.Create(
                    DeploymentTargetId,
                    requiredNodeAffinity: [required],
                    runtimeClassName: runtimeClassName),
            ],
            isEnabled: true,
            isDefault: false,
            new ComputeProfileFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc)));
    }

    private static Kubernetes CreateKubernetes(DelegatingHandler handler) => new(
        new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
        handler);

    private static Task<AdmitDeploymentTargetForWorkloadOutcome>
        AdmitTarget(string deploymentTargetId, CancellationToken cancellationToken = default) => Task.FromResult(
        new AdmitDeploymentTargetForWorkloadOutcome(
            new DeploymentTargetLookupDto(
                deploymentTargetId,
                "Test",
                "test-cluster",
                "test",
                1,
                new DeploymentTargetWorkloadConfigurationLookupDto(
                    1,
                    null,
                    new DeploymentTargetNetworkSourcesDto(
                        ImmutableDictionary<string, DeploymentTargetNetworkSourceDto>.Empty)),
                1,
                Enabled: true,
                AcceptsWorkloads: true,
                DeletionRequested: false,
                HealthStatus: "Healthy",
                TechnicalName: "test-target",
                NetworkPolicy: DeploymentTargetNetworkPolicyDto.Unknown)));

    private static Task<AdmitDeploymentTargetForWorkloadOutcome> RejectTarget(
        string deploymentTargetId,
        CancellationToken cancellationToken = default) => Task.FromResult(
        new AdmitDeploymentTargetForWorkloadOutcome(
            new DeploymentTargetWorkloadAdmissionError(new DeploymentTargetNewWorkloadsDisabled(deploymentTargetId))));

    private sealed class FixedProvider(
        IKubernetes kubernetes
    ) : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(string deploymentTargetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(kubernetes);

        public Task<IKubernetes> GetForCleanupAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) => GetAsync(deploymentTargetId, cancellationToken);

        public Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The default client must not be used.");
    }

    private sealed class ThrowIfUsedProvider : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(string deploymentTargetId, CancellationToken cancellationToken = default) =>
            throw new XunitException("Kubernetes must not be resolved after deployment-target admission fails.");

        public Task<IKubernetes> GetForCleanupAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) => GetAsync(deploymentTargetId, cancellationToken);

        public Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default) =>
            throw new XunitException("The default Kubernetes client must not be resolved.");
    }

    private sealed class ConnectionUnavailableProvider : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(string deploymentTargetId, CancellationToken cancellationToken = default) =>
            Task.FromException<IKubernetes>(
                new DeploymentTargetUnavailableException(
                    deploymentTargetId,
                    DeploymentTargetUnavailableReason.ConnectionUnavailable));

        public Task<IKubernetes> GetForCleanupAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) => GetAsync(deploymentTargetId, cancellationToken);

        public Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<IKubernetes>(
                new DeploymentTargetUnavailableException(
                    null,
                    DeploymentTargetUnavailableReason.ConnectionUnavailable));
    }

    private sealed class SchedulingApiHandler(
        bool runtimeClassExists,
        string nodePool,
        int gpuCapacity
    ) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var response = path switch
            {
                "/apis/node.k8s.io/v1/runtimeclasses/kata" when runtimeClassExists => Json(
                    HttpStatusCode.OK,
                    """
                    {
                      "apiVersion": "node.k8s.io/v1",
                      "kind": "RuntimeClass",
                      "metadata": { "name": "kata" },
                      "handler": "kata",
                      "scheduling": {
                        "nodeSelector": { "pole.io/runtime": "kata" }
                      }
                    }
                    """),
                "/apis/node.k8s.io/v1/runtimeclasses/kata" => Json(
                    HttpStatusCode.NotFound,
                    """
                    {
                      "apiVersion": "v1",
                      "kind": "Status",
                      "status": "Failure",
                      "reason": "NotFound",
                      "message": "provider-secret",
                      "code": 404
                    }
                    """),
                "/api/v1/nodes" => Json(
                    HttpStatusCode.OK,
                    $$"""
                      {
                        "apiVersion": "v1",
                        "kind": "NodeList",
                        "items": [
                          {
                            "apiVersion": "v1",
                            "kind": "Node",
                            "metadata": {
                              "name": "worker-1",
                              "labels": {
                                "kubernetes.io/arch": "amd64",
                                "pole.io/node-pool": "{{nodePool}}",
                                "pole.io/runtime": "kata"
                              }
                            },
                            "spec": { "unschedulable": false },
                            "status": {
                              "allocatable": { "nvidia.com/gpu": "{{gpuCapacity}}" }
                            }
                          }
                        ]
                      }
                      """),
                _ => Json(HttpStatusCode.NotFound, "{}"),
            };
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) => new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
