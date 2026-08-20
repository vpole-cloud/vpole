using System.Net;
using System.Text;
using k8s;
using MongoDB.Bson;
using Pole.Features.ComputeProfiles.Application.Validation;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.ResultType;
using Xunit;

namespace Pole.Tests.Integration.ComputeProfiles;

public sealed class ComputeProfileTargetMappingValidatorTests
{
    [Fact]
    public async Task Mapping_matching_runtime_class_architecture_affinity_and_extended_resource_succeeds()
    {
        var kubernetes = CreateKubernetes(new SchedulingApiHandler(
            runtimeClassExists: true,
            nodePool: "accelerated",
            gpuCapacity: 2));
        var validator = new KubernetesComputeProfileTargetMappingValidator(
            new FixedProvider(kubernetes),
            new AvailableTargetLookup());

        var result = await validator.InvokeAsync(CreateProfile(runtimeClassName: "kata"));

        Assert.IsAssignableFrom<ISuccess>(result);
    }

    [Fact]
    public async Task Mapping_without_a_node_satisfying_extended_resources_is_semantically_invalid()
    {
        var kubernetes = CreateKubernetes(new SchedulingApiHandler(
            runtimeClassExists: true,
            nodePool: "accelerated",
            gpuCapacity: 0));
        var validator = new KubernetesComputeProfileTargetMappingValidator(
            new FixedProvider(kubernetes),
            new AvailableTargetLookup());

        var result = await validator.InvokeAsync(CreateProfile(runtimeClassName: "kata"));

        var failure = Assert.IsType<SemanticValidationFailure>(result);
        Assert.Contains("TargetMappings[0].Scheduling", failure.ValidationErrors.Keys);
    }

    [Fact]
    public async Task Missing_runtime_class_is_semantically_invalid_without_exposing_provider_response()
    {
        var kubernetes = CreateKubernetes(new SchedulingApiHandler(
            runtimeClassExists: false,
            nodePool: "accelerated",
            gpuCapacity: 2));
        var validator = new KubernetesComputeProfileTargetMappingValidator(
            new FixedProvider(kubernetes),
            new AvailableTargetLookup());

        var result = await validator.InvokeAsync(CreateProfile(runtimeClassName: "kata"));

        var failure = Assert.IsType<SemanticValidationFailure>(result);
        Assert.Contains("TargetMappings[0].RuntimeClassName", failure.ValidationErrors.Keys);
        Assert.DoesNotContain("provider-secret", failure.Message, StringComparison.Ordinal);
    }

    private static ComputeProfile CreateProfile(string? runtimeClassName)
    {
        var required = ComputeProfileNodeAffinityTerm.Create(
        [
            ComputeProfileNodeAffinityExpression.Create(
                "pole.io/node-pool",
                ComputeNodeAffinityOperator.In,
                ["accelerated"])
        ]);
        return ComputeProfileFactory.Create(
            "Accelerated",
            string.Empty,
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
                    runtimeClassName: runtimeClassName)
            ],
            isEnabled: true,
            isDefault: false,
            new ComputeProfileFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc)));
    }

    private static Kubernetes CreateKubernetes(DelegatingHandler handler) =>
        new(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);

    private const string DeploymentTargetId = "507f1f77bcf86cd799439071";

    private sealed class FixedProvider(IKubernetes kubernetes)
        : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(kubernetes);

        public Task<IKubernetes> GetDefaultAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The default client must not be used.");
    }

    private sealed class AvailableTargetLookup : IDeploymentTargetLookup
    {
        public Task<Result<DeploymentTargetLookupDto>> GetAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<DeploymentTargetLookupDto>>(CreateTarget(deploymentTargetId));

        public Task<Result<DeploymentTargetLookupDto>> GetDefaultAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<DeploymentTargetLookupDto>>(CreateTarget(DeploymentTargetId));

        public Task<Result<DeploymentTargetLookupDto>> ResolveForNewWorkloadAsync(
            string? deploymentTargetId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<DeploymentTargetLookupDto>>(
                CreateTarget(deploymentTargetId ?? DeploymentTargetId));

        public Task<Result<IReadOnlyList<DeploymentTargetLookupDto>>>
            ListEligibleForNewWorkloadAsync(
                CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<IReadOnlyList<DeploymentTargetLookupDto>>>(
                new[] { CreateTarget(DeploymentTargetId) });

        private static DeploymentTargetLookupDto CreateTarget(string id) =>
            new(
                id,
                "Test",
                "test-cluster",
                "test",
                1,
                new DeploymentTargetWorkloadConfigurationLookupDto(1, null),
                1,
                Enabled: true,
                AcceptsWorkloads: true,
                DeletionRequested: false,
                HealthStatus: "Healthy");
    }

    private sealed class SchedulingApiHandler(
        bool runtimeClassExists,
        string nodePool,
        int gpuCapacity)
        : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var response = path switch
            {
                "/apis/node.k8s.io/v1/runtimeclasses/kata" when runtimeClassExists =>
                    Json(HttpStatusCode.OK,
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
                "/apis/node.k8s.io/v1/runtimeclasses/kata" =>
                    Json(HttpStatusCode.NotFound,
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
                "/api/v1/nodes" => Json(HttpStatusCode.OK, $$"""
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
                _ => Json(HttpStatusCode.NotFound, "{}")
            };
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) =>
            new(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }
}
