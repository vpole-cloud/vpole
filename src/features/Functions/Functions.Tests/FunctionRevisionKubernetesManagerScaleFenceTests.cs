using Microsoft.Extensions.DependencyInjection;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using k8s.Models;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Features.Functions.Runtime.Security;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Functions.Protocol;
using Xunit;

namespace Pole.Features.Functions.Tests;

public sealed class FunctionRevisionKubernetesManagerScaleFenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_requires_authorized_ready_secret_projections(bool ready)
    {
        var subject = CreateSubject();
        var reference = new FunctionSecretReference("507f1f77bcf86cd799439099", "source-key");
        subject = subject with
        {
            Revision = subject.Revision with
            {
                Spec = subject.Revision.Spec with
                {
                    Configuration = new FunctionConfiguration(
                        ImmutableDictionary<string, string>.Empty,
                        ImmutableDictionary<string, FunctionSecretReference>.Empty.Add("TOKEN", reference))
                }
            }
        };
        using var handler = new DeploymentHandler(new V1Deployment());
        using var kubernetes = CreateKubernetes(handler);
        var services = new ServiceCollection();
        services.AddScoped<EnsureConsumerSecretProjections>(_ => (request, ct) =>
        {
            Assert.Equal(subject.App.TenantId, request.TenantId);
            Assert.Equal(subject.App.Id, request.ConsumerId);
            Assert.Equal(subject.Placement.ResourceDeploymentId, request.DeploymentId);
            Assert.Equal(reference.Key, Assert.Single(request.References).Key);
            return Task.FromResult<EnsureConsumerSecretProjectionsOutcome>(ready
                ? new ConsumerSecretProjectionKeys(
                    [new(reference.SecretId, reference.Key, "native-projection", "mapped-key", 1)])
                : new ConsumerSecretProjectionPending(reference.SecretId));
        });
        using var provider = services.BuildServiceProvider();
        var manager = new FunctionRevisionKubernetesManager(
            new FixedKubernetesClientProvider(kubernetes),
            (id, ct) => LookupTarget(id, NetworkPolicyReadiness.EnforcementAttested),
            ReadyBaseline, [], provider.GetRequiredService<IServiceScopeFactory>());

        var result = await manager.ApplyAsync(subject);
        Assert.Equal(ready, result is FunctionRevisionKubernetesMutationCompleted);
        Assert.Equal(ready ? 2 : 0, handler.PostRequests.Count);
        if (ready)
        {
            var manifest = handler.PostRequests[0];
            Assert.Contains("native-projection", manifest);
            Assert.Contains("mapped-key", manifest);
            Assert.DoesNotContain(reference.SecretId, manifest);
        }
        else
        {
            var error = Assert.IsAssignableFrom<FunctionRevisionKubernetesOperationError>(result.Value);
            Assert.IsType<FunctionRevisionSecretsUnavailable>(error.Value);

            ready = true;
            Assert.IsType<FunctionRevisionKubernetesMutationCompleted>((await manager.ApplyAsync(subject)).Value);
            Assert.Equal(2, handler.PostRequests.Count);
            Assert.Contains("native-projection", handler.PostRequests[0]);
            Assert.Contains("mapped-key", handler.PostRequests[0]);
        }
    }

    [Theory]
    [InlineData(NetworkPolicyReadiness.EnforcementRequired, false)]
    [InlineData(NetworkPolicyReadiness.EnforcementAttested, true)]
    [InlineData(NetworkPolicyReadiness.IsolationNotEnforced, true)]
    public async Task Apply_checks_current_readiness_before_any_Kubernetes_write(
        NetworkPolicyReadiness readiness,
        bool allowed)
    {
        var subject = CreateSubject();
        using var handler = new DeploymentHandler(Deployment(subject, 0, 0));
        using var kubernetes = CreateKubernetes(handler);
        var currentReadiness = readiness;
        var lookups = 0;
        var manager = new FunctionRevisionKubernetesManager(
            new FixedKubernetesClientProvider(kubernetes),
            (id, ct) =>
            {
                lookups++;
                Assert.Equal(subject.Placement.DeploymentTargetId, id);
                return LookupTarget(id, currentReadiness);
            },
            ReadyBaseline,
            [],
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        var result = await manager.ApplyAsync(subject);
        Assert.Equal(allowed, result is FunctionRevisionKubernetesMutationCompleted);
        Assert.Equal(allowed ? 2 : 0, handler.PostRequests.Count);
        currentReadiness = NetworkPolicyReadiness.EnforcementRequired;
        Assert.IsAssignableFrom<FunctionRevisionKubernetesOperationError>(
            (await manager.ApplyAsync(subject)).Value);
        Assert.Equal(2, lookups);
        Assert.Equal(allowed ? 2 : 0, handler.PostRequests.Count);
    }

    [Fact]
    public async Task Apply_blocks_when_runtime_cell_baseline_cannot_be_repaired()
    {
        var subject = CreateSubject();
        using var handler = new DeploymentHandler(Deployment(subject, 0, 0));
        using var kubernetes = CreateKubernetes(handler);
        var manager = new FunctionRevisionKubernetesManager(
            new FixedKubernetesClientProvider(kubernetes),
            (id, ct) => LookupTarget(id, NetworkPolicyReadiness.EnforcementAttested),
(request, ct) =>
                Task.FromResult<EnsureRuntimeCellIngressBaselineOutcome>(
                    new RuntimeCellIngressBaselinePolicyUnavailable(request.RuntimeCellId)),
            [],
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

        var result = await manager.ApplyAsync(subject);

        Assert.IsAssignableFrom<FunctionRevisionKubernetesOperationError>(result.Value);
        Assert.Empty(handler.PostRequests);
    }

    [Fact]
    public async Task Missing_gateway_source_blocks_apply_and_positive_scale_but_allows_downscale()
    {
        var subject = CreateSubject();
        using var handler = new DeploymentHandler(Deployment(subject, 1, 4));
        using var kubernetes = CreateKubernetes(handler);
        var manager = new FunctionRevisionKubernetesManager(
            new FixedKubernetesClientProvider(kubernetes),
            (id, ct) => LookupTarget(id, NetworkPolicyReadiness.EnforcementAttested, gatewaySource: false),
            ReadyBaseline,
            [],
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

        var apply = await manager.ApplyAsync(subject);
        var provisioningIncrease = await manager.ScaleProvisioningAsync(subject, 2);
        var scaleIncrease = await manager.ScaleAsync(subject, 2, 5);
        var downscale = await manager.ScaleAsync(subject, 0, 5);

        foreach (var result in new[] { apply, provisioningIncrease, scaleIncrease })
        {
            var unavailable = Assert.IsType<FunctionRevisionGatewaySourceUnavailable>(
                Assert.IsType<FunctionRevisionKubernetesOperationError>(result.Value).Value);
            Assert.Contains("gateway source", unavailable.Message, StringComparison.Ordinal);
        }
        Assert.IsType<FunctionRevisionKubernetesMutationCompleted>(downscale.Value);
        Assert.Empty(handler.PostRequests);
        Assert.Single(handler.PutRequests);
    }

    [Theory]
    [InlineData(false, 1, 3, false)]
    [InlineData(false, 3, 1, true)]
    [InlineData(false, 3, 0, true)]
    [InlineData(true, 1, 3, false)]
    [InlineData(true, 3, 1, true)]
    [InlineData(true, 3, 0, true)]
    public async Task Revoked_target_rejects_increases_but_preserves_downscale(
        bool provisioning,
        int current,
        int desired,
        bool allowed)
    {
        var subject = CreateSubject();
        using var handler = new DeploymentHandler(Deployment(subject, current, 4));
        using var kubernetes = CreateKubernetes(handler);
        var manager = new FunctionRevisionKubernetesManager(
            new FixedKubernetesClientProvider(kubernetes),
            (id, ct) => LookupTarget(id, NetworkPolicyReadiness.EnforcementRequired),
            ReadyBaseline,
            [],
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        var result = provisioning
            ? await manager.ScaleProvisioningAsync(subject, desired)
            : await manager.ScaleAsync(subject, desired, 5);
        Assert.Equal(allowed, result is FunctionRevisionKubernetesMutationCompleted);
        Assert.Equal(allowed ? 1 : 0, handler.PutRequests.Count);
    }

    [Fact]
    public async Task Read_and_delete_do_not_require_readiness_lookup()
    {
        var subject = CreateSubject();
        using var handler = new DeploymentHandler(Deployment(subject, 1, 4));
        using var kubernetes = CreateKubernetes(handler);
        var manager = new FunctionRevisionKubernetesManager(
            new FixedKubernetesClientProvider(kubernetes),
            (_, _) => throw new InvalidOperationException(
                "Readiness lookup is forbidden for diagnostics/deletion."),
            ReadyBaseline,
            [],
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        Assert.IsType<FunctionReplicaState>((await manager.ReadReplicaStateAsync(subject)).Value);
        Assert.IsType<FunctionRevisionKubernetesMutationCompleted>((await manager.DeleteAsync(subject)).Value);
        Assert.Equal(2, handler.DeleteRequests);
    }

    [Fact]
    public async Task Newer_generation_replaces_replicas_and_annotation_in_one_request()
    {
        var subject = CreateSubject();
        using var handler = new DeploymentHandler(Deployment(subject, replicas: 1, generation: 4));
        using var kubernetes = CreateKubernetes(handler);
        var manager = CreateManager(kubernetes);

        var result = await manager.ScaleAsync(subject, replicas: 3, coordinationGeneration: 5);

        Assert.IsType<FunctionRevisionKubernetesMutationCompleted>(result.Value);
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

        var failure = result switch
        {
            FunctionRevisionKubernetesOperationError error => error,
            FunctionRevisionKubernetesMutationCompleted => throw new Xunit.Sdk.XunitException("Expected a scale rejection.")
        };
        Assert.True(failure is FunctionRevisionKubernetesOperationRejected);
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

        Assert.IsType<FunctionRevisionKubernetesMutationCompleted>(result.Value);
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

        var failure = result switch
        {
            FunctionRevisionKubernetesOperationError error => error,
            FunctionRevisionKubernetesMutationCompleted => throw new Xunit.Sdk.XunitException("Expected a scale rejection.")
        };
        Assert.True(failure is FunctionRevisionKubernetesOperationRejected);
        Assert.Empty(handler.PutRequests);
    }

    [Fact]
    public async Task Provisioning_scale_rereads_resource_version_after_a_conflict()
    {
        var subject = CreateSubject();
        using var handler = new DeploymentHandler(Deployment(subject, replicas: 0, generation: 0))
        {
            ConflictsRemaining = 1
        };
        using var kubernetes = CreateKubernetes(handler);
        var baselineChecks = 0;
        var manager = CreateManager(kubernetes, baseline: (request, ct) =>
        {
            baselineChecks++;
            return ReadyBaseline(request, ct);
        });

        var result = await manager.ScaleProvisioningAsync(subject, replicas: 1);

        Assert.IsType<FunctionRevisionKubernetesMutationCompleted>(result.Value);
        Assert.Equal(2, handler.GetRequests);
        Assert.Equal(2, handler.PutRequests.Count);
        Assert.Equal(2, baselineChecks);
        Assert.Equal(["17", "18"], handler.PutRequests.Select(request =>
        {
            using var body = JsonDocument.Parse(request.Body);
            return body.RootElement.GetProperty("metadata").GetProperty("resourceVersion").GetString();
        }));
    }

    [Fact]
    public async Task Provisioning_scale_does_not_write_when_already_current()
    {
        var subject = CreateSubject();
        using var handler = new DeploymentHandler(Deployment(subject, replicas: 1, generation: 0));
        using var kubernetes = CreateKubernetes(handler);
        var manager = CreateManager(kubernetes, (_, _) =>
            throw new InvalidOperationException("An unchanged scale does not require increase admission."));

        var result = await manager.ScaleProvisioningAsync(subject, replicas: 1);

        Assert.IsType<FunctionRevisionKubernetesMutationCompleted>(result.Value);
        Assert.Equal(1, handler.GetRequests);
        Assert.Empty(handler.PutRequests);
    }

    [Fact]
    public async Task Provisioning_scale_accepts_another_writer_converging_after_conflict()
    {
        var subject = CreateSubject();
        var deployment = Deployment(subject, replicas: 0, generation: 0);
        using var handler = new DeploymentHandler(deployment)
        {
            ConflictsRemaining = 1,
            OnConflict = () => deployment.Spec.Replicas = 1
        };
        using var kubernetes = CreateKubernetes(handler);
        var manager = CreateManager(kubernetes);

        var result = await manager.ScaleProvisioningAsync(subject, replicas: 1);

        Assert.IsType<FunctionRevisionKubernetesMutationCompleted>(result.Value);
        Assert.Equal(2, handler.GetRequests);
        Assert.Single(handler.PutRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provisioning_scale_revalidates_target_and_baseline_after_conflict(bool revokeBaseline)
    {
        var subject = CreateSubject();
        var revoked = false;
        using var handler = new DeploymentHandler(Deployment(subject, replicas: 0, generation: 0))
        {
            ConflictsRemaining = 1,
            OnConflict = () => revoked = true
        };
        using var kubernetes = CreateKubernetes(handler);
        var targetChecks = 0;
        var baselineChecks = 0;
        var manager = CreateManager(kubernetes, (id, _) =>
        {
            targetChecks++;
            return LookupTarget(id, revoked && !revokeBaseline
                ? NetworkPolicyReadiness.EnforcementRequired : NetworkPolicyReadiness.EnforcementAttested);
        }, (_, _) =>
        {
            baselineChecks++;
            return Task.FromResult<EnsureRuntimeCellIngressBaselineOutcome>(revoked && revokeBaseline
                ? new RuntimeCellIngressBaselineNotReady(subject.Placement.RuntimeCellId)
                : new RuntimeCellIngressBaselineReady());
        });

        var result = await manager.ScaleProvisioningAsync(subject, replicas: 1);

        var error = Assert.IsAssignableFrom<FunctionRevisionKubernetesOperationError>(result.Value);
        Assert.IsType<FunctionRevisionKubernetesOperationRejected>(error.Value);
        Assert.Equal(2, handler.GetRequests);
        Assert.Single(handler.PutRequests);
        Assert.Equal(2, targetChecks);
        Assert.Equal(revokeBaseline ? 2 : 1, baselineChecks);
    }

    [Fact]
    public async Task Provisioning_scale_conflict_retries_are_bounded()
    {
        var subject = CreateSubject();
        using var handler = new DeploymentHandler(Deployment(subject, replicas: 0, generation: 0))
        {
            ConflictsRemaining = 10
        };
        using var kubernetes = CreateKubernetes(handler);
        var manager = CreateManager(kubernetes);

        var result = await manager.ScaleProvisioningAsync(subject, replicas: 1);

        var error = Assert.IsAssignableFrom<FunctionRevisionKubernetesOperationError>(result.Value);
        Assert.IsType<FunctionRevisionKubernetesOperationRejected>(error.Value);
        Assert.Equal(5, handler.GetRequests);
        Assert.Equal(5, handler.PutRequests.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task Provisioning_scale_preserves_cancellation_after_conflict(int cancelAfter)
    {
        var subject = CreateSubject();
        using var cancellation = new CancellationTokenSource();
        using var handler = new DeploymentHandler(Deployment(subject, replicas: 0, generation: 0))
        {
            ConflictsRemaining = 10
        };
        handler.OnConflict = () =>
        {
            if (handler.PutRequests.Count == cancelAfter)
                cancellation.Cancel();
        };
        using var kubernetes = CreateKubernetes(handler);
        var manager = CreateManager(kubernetes);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.ScaleProvisioningAsync(subject, 1, cancellation.Token));

        Assert.Equal(cancelAfter, handler.PutRequests.Count);
        Assert.Equal(cancelAfter, handler.GetRequests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Provisioning_scale_does_not_retry_other_http_failures(HttpStatusCode status)
    {
        var subject = CreateSubject();
        using var handler = new DeploymentHandler(Deployment(subject, replicas: 0, generation: 0))
        {
            PutFailureStatus = status
        };
        using var kubernetes = CreateKubernetes(handler);
        var manager = CreateManager(kubernetes);

        var result = await manager.ScaleProvisioningAsync(subject, replicas: 1);

        var error = Assert.IsAssignableFrom<FunctionRevisionKubernetesOperationError>(result.Value);
        Assert.IsType<FunctionRevisionKubernetesUnavailable>(error.Value);
        Assert.Single(handler.PutRequests);
        Assert.Equal(1, handler.GetRequests);
    }

    private static FunctionRevisionKubernetesManager CreateManager(
        IKubernetes kubernetes,
        LookupDeploymentTarget? lookup = null,
        EnsureRuntimeCellIngressBaseline? baseline = null) =>
        new(
            new FixedKubernetesClientProvider(kubernetes),
            lookup ?? ((id, ct) => LookupTarget(id, NetworkPolicyReadiness.EnforcementAttested)),
            baseline ?? ReadyBaseline,
            Array.Empty<IFunctionAgentTrustBundleProvider>(),
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

    private static Task<EnsureRuntimeCellIngressBaselineOutcome> ReadyBaseline(
        EnsureRuntimeCellIngressBaselineRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult<EnsureRuntimeCellIngressBaselineOutcome>(
            new RuntimeCellIngressBaselineReady());

    private static Task<LookupDeploymentTargetOutcome> LookupTarget(
        string id,
        NetworkPolicyReadiness readiness,
        bool gatewaySource = true) =>
        Task.FromResult<LookupDeploymentTargetOutcome>(
            new LookupDeploymentTargetOutcome(new DeploymentTargetLookupDto(
                id,
                "Target",
                "cluster",
                "InCluster",
                1,
                new(1, null, new DeploymentTargetNetworkSourcesDto(
                    gatewaySource
                        ? ImmutableDictionary<string, DeploymentTargetNetworkSourceDto>.Empty.Add(
                            "functions/gateway", new DeploymentTargetCidrNetworkSourceDto("192.0.2.9/32"))
                        : ImmutableDictionary<string, DeploymentTargetNetworkSourceDto>.Empty)),
                1,
                true,
                true,
                false,
                "Healthy",
        TechnicalName: "test-target",
        NetworkPolicy: new(
                    NetworkPolicyEnforcementStatus.Unknown,
                    readiness,
                    NetworkPolicyReason.EnforcementNotEstablished,
                    DateTime.UnixEpoch,
                    NetworkPolicyEvidenceSource.OperatorAttestation,
                    false))));

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
            new FunctionImage("registry.example", "team/functions", "v1",
                Digest: null,
                PullSecretName: null),
            PoleFunctionProtocol.Version,
            new FunctionConfiguration(
                ImmutableDictionary<string, string>.Empty,
                ImmutableDictionary<string, FunctionSecretReference>.Empty),
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
            new FunctionPlacementIdentity(
                "507f1f77bcf86cd799439091",
                "507f1f77bcf86cd799439092",
                "507f1f77bcf86cd799439093"),
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
        return new FunctionRevisionInKubernetes(
            app,
            revision,
            new FunctionResourcePlacement(
                app.Placement.ResourceDeploymentId,
                app.Placement.RuntimeCellId,
                app.Placement.DeploymentTargetId,
                "pole-tenant-rg",
                "507f1f77bcf86cd799439044"));
    }

    private sealed record RecordedPut(
        string Path,
        string Body);

    private sealed class FixedKubernetesClientProvider(IKubernetes kubernetes) : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(kubernetes);

        public Task<IKubernetes> GetForCleanupAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) =>
            GetAsync(deploymentTargetId, cancellationToken);

        public Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(kubernetes);
    }

    private sealed class DeploymentHandler(V1Deployment deployment) : DelegatingHandler
    {
        public List<RecordedPut> PutRequests { get; } = [];
        public List<string> PostRequests { get; } = [];
        public int DeleteRequests { get; private set; }
        public int GetRequests { get; private set; }
        public int ConflictsRemaining { get; set; }
        public Action? OnConflict { get; set; }
        public HttpStatusCode? PutFailureStatus { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                GetRequests++;
                return JsonResponse(request, Serialize(deployment));
            }

            if (request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                PostRequests.Add(body);
                return JsonResponse(request, body);
            }
            if (request.Method == HttpMethod.Delete)
            {
                DeleteRequests++;
                return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
            }

            if (request.Method == HttpMethod.Put)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                PutRequests.Add(new RecordedPut(request.RequestUri!.AbsolutePath, body));
                if (PutFailureStatus is { } failureStatus)
                    return new HttpResponseMessage(failureStatus) { RequestMessage = request };

                if (ConflictsRemaining > 0)
                {
                    ConflictsRemaining--;
                    deployment.Metadata.ResourceVersion =
                        (int.Parse(deployment.Metadata.ResourceVersion, CultureInfo.InvariantCulture) + 1)
                        .ToString(CultureInfo.InvariantCulture);
                    OnConflict?.Invoke();
                    return new HttpResponseMessage(HttpStatusCode.Conflict) { RequestMessage = request };
                }

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
