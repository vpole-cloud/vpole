using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using k8s.Models;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Features.Functions.Runtime.Security;
using Pole.Functions.Protocol;
using Pole.ResultType;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

public sealed class FunctionRevisionKubernetesManagerScaleFenceTests
{
    [Fact]
    public async Task Newer_generation_replaces_replicas_and_annotation_in_one_request()
    {
        var subject = CreateSubject();
        using var handler = new DeploymentHandler(Deployment(subject, replicas: 1, generation: 4));
        using var kubernetes = CreateKubernetes(handler);
        var manager = CreateManager(kubernetes);

        var result = await manager.ScaleAsync(subject, replicas: 3, coordinationGeneration: 5);

        Assert.True(result is ISuccess, result.ToString());
        var request = Assert.Single(handler.PutRequests);
        Assert.EndsWith(
            $"/apis/apps/v1/namespaces/{subject.Namespace}/deployments/{subject.DeploymentName}",
            request.Path,
            StringComparison.Ordinal);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(3, body.RootElement.GetProperty("spec").GetProperty("replicas").GetInt32());
        Assert.Equal(
            "5",
            body.RootElement.GetProperty("metadata")
                .GetProperty("annotations")
                .GetProperty(FunctionRevisionInKubernetes.ScaleGenerationAnnotation)
                .GetString());
    }

    [Fact]
    public async Task Stale_generation_returns_conflict_without_replace_request()
    {
        var subject = CreateSubject();
        using var handler = new DeploymentHandler(Deployment(subject, replicas: 3, generation: 6));
        using var kubernetes = CreateKubernetes(handler);
        var manager = CreateManager(kubernetes);

        var result = await manager.ScaleAsync(subject, replicas: 2, coordinationGeneration: 5);

        Assert.True(result is ConflictFailure, result.ToString());
        Assert.Empty(handler.PutRequests);
    }

    [Fact]
    public async Task Same_generation_and_replicas_is_idempotent_without_replace_request()
    {
        var subject = CreateSubject();
        using var handler = new DeploymentHandler(Deployment(subject, replicas: 3, generation: 5));
        using var kubernetes = CreateKubernetes(handler);
        var manager = CreateManager(kubernetes);

        var result = await manager.ScaleAsync(subject, replicas: 3, coordinationGeneration: 5);

        Assert.True(result is ISuccess, result.ToString());
        Assert.Empty(handler.PutRequests);
    }

    [Fact]
    public async Task Unfenced_bootstrap_scale_rejects_a_serving_revision()
    {
        var provisioning = CreateSubject();
        var subject = provisioning with
        {
            Revision = provisioning.Revision.MarkReady(DateTimeOffset.UtcNow).Activate()
        };
        using var handler = new DeploymentHandler(Deployment(subject, replicas: 1, generation: 4));
        using var kubernetes = CreateKubernetes(handler);
        var manager = CreateManager(kubernetes);

        var result = await manager.ScaleProvisioningAsync(subject, replicas: 0);

        Assert.IsType<ConflictFailure>(result);
        Assert.Empty(handler.PutRequests);
    }

    private static FunctionRevisionKubernetesManager CreateManager(IKubernetes kubernetes) =>
        new(kubernetes, Array.Empty<IFunctionAgentTrustBundleProvider>());

    private static Kubernetes CreateKubernetes(DelegatingHandler handler) =>
        new(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);

    private static V1Deployment Deployment(
        FunctionRevisionInKubernetes subject,
        int replicas,
        long generation)
    {
        var deployment = subject.ToDeployment();
        deployment.Metadata.ResourceVersion = "17";
        deployment.Spec.Replicas = replicas;
        deployment.Metadata.Annotations![FunctionRevisionInKubernetes.ScaleGenerationAnnotation] =
            generation.ToString(CultureInfo.InvariantCulture);
        return deployment;
    }

    private static FunctionRevisionInKubernetes CreateSubject()
    {
        var spec = new FunctionRevisionSpecInput(
            new FunctionImage("registry.example", "team/functions", "v1"),
            PoleFunctionProtocol.Version,
            new FunctionConfiguration(
                ImmutableDictionary<string, string>.Empty,
                ImmutableDictionary<string, string>.Empty),
            new FunctionResources(
                new FunctionResourceValues(100, 128),
                new FunctionResourceValues(500, 512)),
            new FunctionScalingPolicy(0, 5, 4, 8, 60, 30),
            [
                new FunctionHandlerSpec(
                    "webhook",
                    4,
                    new HttpFunctionTriggerSpec(
                        string.Empty,
                        "orders/{id}",
                        ["POST"],
                        FunctionExposure.Public))
            ]);
        var created = FunctionAppFactory.Create(
            "tenant-1",
            "resource-group-1",
            "pole-tenant-rg",
            "order-functions",
            "orders",
            spec,
            [],
            DateTimeOffset.Parse("2026-07-18T10:00:00Z", CultureInfo.InvariantCulture));
        var app = created.FunctionApp with { Id = "507f1f77bcf86cd799439011" };
        var revision = created.Revision with
        {
            Id = "507f1f77bcf86cd799439012",
            FunctionAppId = app.Id,
            RevisionNumber = 3
        };
        return new FunctionRevisionInKubernetes(app, revision);
    }

    private sealed record RecordedPut(string Path, string Body);

    private sealed class DeploymentHandler(V1Deployment deployment) : DelegatingHandler
    {
        public List<RecordedPut> PutRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return JsonResponse(request, Serialize(deployment));

            if (request.Method == HttpMethod.Put)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                PutRequests.Add(new RecordedPut(request.RequestUri!.AbsolutePath, body));
                return JsonResponse(request, body);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        }

        private static HttpResponseMessage JsonResponse(HttpRequestMessage request, string body) =>
            new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
            RequestMessage = request
        };

        private static string Serialize(V1Deployment value) =>
            JsonSerializer.Serialize(new
            {
                apiVersion = value.ApiVersion,
                kind = value.Kind,
                metadata = new
                {
                    name = value.Metadata.Name,
                    @namespace = value.Metadata.NamespaceProperty,
                    resourceVersion = value.Metadata.ResourceVersion,
                    annotations = value.Metadata.Annotations
                },
                spec = new { replicas = value.Spec.Replicas }
            });
    }
}
