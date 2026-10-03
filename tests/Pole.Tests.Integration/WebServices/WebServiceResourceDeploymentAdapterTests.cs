using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.Core.Kubernetes;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Application.Persistence.Deployments;
using Pole.Features.WebServices.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Pole.Tests.Integration.ResourceDeployments;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

public sealed class WebServiceResourceDeploymentAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_rejects_unavailable_or_pending_Secret_reference_before_any_native_workload_write(bool pending)
    {
        var webService = CreateWebService();
        webService = webService with
        {
            Spec = webService.Spec with
            {
                Config = webService.Spec.Config with
                {
                    SecretEnvironmentReferences = ImmutableDictionary<string, SecretEnvironmentReference>.Empty.Add("APPLICATION_TOKEN", new("secret", "token"))
                }
            }
        };
        var reader = new RecordingReader(webService);
        using var handler = new RecordingKubernetesHandler();
        using var kubernetes = new Kubernetes(new KubernetesClientConfiguration { Host = "http://kubernetes.test" }, handler);
        var subject = new WebServiceResourceDeploymentAdapter(reader.LoadSnapshot, reader.LoadById, reader.LoadExactDeployment, UnexpectedRegistryAccess,
            new FixedKubernetesClientProvider(kubernetes), (_, _) => throw new InvalidOperationException("Unresolved references must not reach policy or workload writes."), EmptyConnectionReferences,
            (tenant, type, consumer, deployment, references, _) =>
            {
                Assert.Equal(TenantId, tenant); Assert.Equal(ResourceId, consumer); Assert.Equal(DeploymentId, deployment);
                Assert.Equal("token", Assert.Single(references).Key);
                return Task.FromResult<Pole.Features.Secrets.Contracts.EnsureConsumerSecretProjectionsOutcome>(pending
                    ? new Pole.Features.Secrets.Contracts.ConsumerSecretProjectionPending("secret")
                    : new Pole.Features.Secrets.Contracts.ConsumerSecretReferenceUnavailable("secret", "SecretBindingUnavailable"));
            });
        var result = Assert.IsType<ResourceDeploymentAdapterResult.Failed>(await subject.ApplyAsync(CreateContext(includeIngress: true)));
        Assert.Equal(pending ? "SecretProjectionPending" : "SecretConfigurationUnavailable", result.FailureCode);
        Assert.Empty(handler.Requests);
    }

    private static Task<Pole.Features.ResourceConnections.Contracts.ResolveResourceConnectionReferencesOutcome> EmptyConnectionReferences(
        string tenantId, ResourceTypeId type, string sourceId, string deploymentId,
        IReadOnlyCollection<Pole.Features.ResourceConnections.Contracts.ResourceConnectionValueReference> references, CancellationToken ct) =>
        Task.FromResult<Pole.Features.ResourceConnections.Contracts.ResolveResourceConnectionReferencesOutcome>(new Pole.Features.ResourceConnections.Contracts.ResolvedResourceConnectionValues([]));
    private static Task<Pole.Features.Secrets.Contracts.EnsureConsumerSecretProjectionsOutcome> EmptySecretReferences(
        string tenantId, ResourceTypeId type, string consumerId, string deploymentId,
        IReadOnlyCollection<Pole.Features.Secrets.Contracts.ConsumerSecretKeyReference> references, CancellationToken ct) =>
        Task.FromResult<Pole.Features.Secrets.Contracts.EnsureConsumerSecretProjectionsOutcome>(new Pole.Features.Secrets.Contracts.ConsumerSecretProjectionKeys([]));

    [Theory]
    [InlineData("0", "TCP")]
    [InlineData("65536", "TCP")]
    [InlineData("8080", "UDP")]
    public async Task Apply_RejectsInvalidPublicIngressTargetPort(string targetPort, string protocol)
    {
        var reader = new RecordingReader(CreateWebService(targetPort, protocol));
        using var handler = new RecordingKubernetesHandler();
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" }, handler);
        var subject = new WebServiceResourceDeploymentAdapter(
            reader.LoadSnapshot,
            reader.LoadById,
            reader.LoadExactDeployment,
            UnexpectedRegistryAccess,
            new FixedKubernetesClientProvider(kubernetes),
            (_, _) => throw new InvalidOperationException("Invalid ports must not reach the policy writer."),
            EmptyConnectionReferences,
            EmptySecretReferences);

        var result = await subject.ApplyAsync(CreateContext(includeIngress: true));

        var failure = Assert.IsType<ResourceDeploymentAdapterResult.Failed>(result);
        Assert.Equal("PublicIngressTargetPortUnavailable", failure.FailureCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Apply_UsesContextClientNamespaceAndResolvedIngress()
    {
        var webService = CreateWebService();
        var reader = new RecordingReader(webService);
        var policyRequests = new List<ResourceIngressPolicyRequest>();
        Task<ReconcileResourceIngressPolicyOutcome> ApplyPolicy(
            ResourceIngressPolicyRequest request,
            CancellationToken cancellationToken = default)
        {
            policyRequests.Add(request);
            return Task.FromResult<ReconcileResourceIngressPolicyOutcome>(new ResourceIngressPolicyApplied());
        }
        using var handler = new RecordingKubernetesHandler
        {
            IngressReadResponse = CurrentIngressJson()
        };
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);
        var subject = new WebServiceResourceDeploymentAdapter(
            reader.LoadSnapshot,
            reader.LoadById,
            reader.LoadExactDeployment,
            UnexpectedRegistryAccess,
            new FixedKubernetesClientProvider(kubernetes),
            ApplyPolicy,
            EmptyConnectionReferences,
            EmptySecretReferences);
        var context = CreateContext(includeIngress: true);

        var result = await subject.ApplyAsync(context);

        var succeeded = Assert.IsType<ResourceDeploymentAdapterResult.Succeeded>(result);
        Assert.Equal(
            ["api.example.test", "www.api.example.test"],
            succeeded.Endpoints.Select(endpoint => endpoint.Host));
        Assert.All(succeeded.Endpoints, endpoint =>
        {
            Assert.Equal("Ingress", endpoint.Kind);
            Assert.Equal(443, endpoint.Port);
            Assert.Equal("https", endpoint.Scheme);
        });
        Assert.Equal(
            ["web-public", "web-http01"],
            policyRequests.Select(request => request.Purpose));
        Assert.Equal([8080], policyRequests[0].AllowedTcpPorts.Select(port => port.Value));
        Assert.Equal([8089], policyRequests[1].AllowedTcpPorts.Select(port => port.Value));
        Assert.Equal(
            [(TenantId, ResourceId, DeploymentId, 1L, 1L)],
            reader.ApplyRequests);
        Assert.Equal(5, handler.Requests.Count);
        Assert.All(
            handler.Requests,
            request => Assert.Contains(
                $"/namespaces/{KubernetesNamespace}/",
                request.Path,
                StringComparison.Ordinal));

        using var ingress = JsonDocument.Parse(
            Assert.Single(
                handler.Requests,
                request => request.Path.EndsWith(
                    "/ingresses",
                    StringComparison.Ordinal)).Body);
        var root = ingress.RootElement;
        Assert.Equal(
            KubernetesNamespace,
            root.GetProperty("metadata").GetProperty("namespace").GetString());
        Assert.Equal(
            "nginx-private",
            root.GetProperty("spec").GetProperty("ingressClassName").GetString());
        Assert.Equal(
            "pole-private-issuer",
            root.GetProperty("metadata")
                .GetProperty("annotations")
                .GetProperty("cert-manager.io/cluster-issuer")
                .GetString());

        using var deployment = JsonDocument.Parse(
            Assert.Single(
                handler.Requests,
                request => request.Path.EndsWith(
                    "/deployments",
                    StringComparison.Ordinal)).Body);
        var annotations = deployment.RootElement
            .GetProperty("spec")
            .GetProperty("template")
            .GetProperty("metadata")
            .GetProperty("annotations");
        Assert.Equal(
            DeploymentId,
            annotations.GetProperty("pole.server/resourceDeploymentId").GetString());
        Assert.Equal(
            RuntimeCellId,
            annotations.GetProperty("pole.server/runtimeCellId").GetString());
        Assert.Equal(
            TargetId,
            annotations.GetProperty("pole.server/deploymentTargetId").GetString());
        Assert.Equal(
            "1",
            annotations.GetProperty("pole.server/deploymentGeneration").GetString());
        var resources = deployment.RootElement
            .GetProperty("spec")
            .GetProperty("template")
            .GetProperty("spec")
            .GetProperty("containers")[0]
            .GetProperty("resources");
        Assert.Equal(
            "250m",
            resources.GetProperty("requests").GetProperty("cpu").GetString());
        Assert.Equal(
            "1",
            resources.GetProperty("limits").GetProperty("cpu").GetString());
    }

    [Fact]
    public async Task Apply_PublicServiceWithoutResolvedIngress_FailsBeforeKubernetes()
    {
        var reader = new RecordingReader(CreateWebService());
        using var handler = new RecordingKubernetesHandler();
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);
        var subject = new WebServiceResourceDeploymentAdapter(
            reader.LoadSnapshot,
            reader.LoadById,
            reader.LoadExactDeployment,
            UnexpectedRegistryAccess,
            new FixedKubernetesClientProvider(kubernetes),
            SuccessfulPolicy,
            EmptyConnectionReferences,
            EmptySecretReferences);

        var result = await subject.ApplyAsync(
            CreateContext(includeIngress: false));

        var failed = Assert.IsType<ResourceDeploymentAdapterResult.Failed>(result);
        Assert.Equal("IngressConfigurationMissing", failed.FailureCode);
        Assert.False(failed.Retryable);
        Assert.Equal(
            [(TenantId, ResourceId, DeploymentId, 1L, 1L)],
            reader.ApplyRequests);
        Assert.Empty(reader.DeleteRequests);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Apply_PrivateServiceDeletesOwnedIngressAndReconcilesDenyTombstone()
    {
        var webService = CreateWebService() with
        {
            Exposure = WebServiceExposure.Create(WebServiceExposureMode.Private)
        };
        var policyRequests = new List<ResourceIngressPolicyRequest>();
        Task<ReconcileResourceIngressPolicyOutcome> ApplyPolicy(
            ResourceIngressPolicyRequest request,
            CancellationToken cancellationToken = default)
        {
            policyRequests.Add(request);
            return Task.FromResult<ReconcileResourceIngressPolicyOutcome>(new ResourceIngressPolicyApplied());
        }
        var reader = new RecordingReader(webService);
        using var handler = new RecordingKubernetesHandler
        {
            IngressReadResponse = CurrentIngressJson()
        };
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);
        var subject = new WebServiceResourceDeploymentAdapter(
            reader.LoadSnapshot,
            reader.LoadById,
            reader.LoadExactDeployment,
            UnexpectedRegistryAccess,
            new FixedKubernetesClientProvider(kubernetes),
            ApplyPolicy,
            EmptyConnectionReferences,
            EmptySecretReferences);

        var result = await subject.ApplyAsync(CreateContext(includeIngress: false));

        var succeeded = Assert.IsType<ResourceDeploymentAdapterResult.Succeeded>(result);
        Assert.Empty(succeeded.Endpoints);
        Assert.Equal(2, policyRequests.Count);
        Assert.All(policyRequests, request => Assert.Empty(request.AllowedTcpPorts));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Delete &&
            request.Path.EndsWith($"/ingresses/{WebServiceInKubernetes.BuildIngressName(webService.Name)}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Apply_PrivateSupersededGenerationDoesNotDeleteIngress()
    {
        var webService = CreateWebService() with
        {
            Exposure = WebServiceExposure.Create(WebServiceExposureMode.Private)
        };
        var reader = new RecordingReader(webService) { CurrentGeneration = 2 };
        using var handler = new RecordingKubernetesHandler
        {
            IngressReadResponse = CurrentIngressJson()
        };
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);
        var subject = new WebServiceResourceDeploymentAdapter(
            reader.LoadSnapshot,
            reader.LoadById,
            reader.LoadExactDeployment,
            UnexpectedRegistryAccess,
            new FixedKubernetesClientProvider(kubernetes),
            SuccessfulPolicy,
            EmptyConnectionReferences,
            EmptySecretReferences);

        var result = await subject.ApplyAsync(CreateContext(includeIngress: false));

        var failed = Assert.IsType<ResourceDeploymentAdapterResult.Failed>(result);
        Assert.Equal("DeploymentGenerationSuperseded", failed.FailureCode);
        Assert.True(failed.Retryable);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Apply_PublicSupersededGenerationDoesNotCreateIngress()
    {
        var reader = new RecordingReader(CreateWebService()) { CurrentGeneration = 2 };
        using var handler = new RecordingKubernetesHandler
        {
            IngressReadResponse = CurrentIngressJson()
        };
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);
        var subject = new WebServiceResourceDeploymentAdapter(
            reader.LoadSnapshot,
            reader.LoadById,
            reader.LoadExactDeployment,
            UnexpectedRegistryAccess,
            new FixedKubernetesClientProvider(kubernetes),
            SuccessfulPolicy,
            EmptyConnectionReferences,
            EmptySecretReferences);

        var result = await subject.ApplyAsync(CreateContext(includeIngress: true));

        Assert.IsType<ResourceDeploymentAdapterResult.Failed>(result);
        Assert.DoesNotContain(handler.Requests, request =>
            request.Path.EndsWith("/ingresses", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Apply_PublicGenerationSupersededDuringIngressReadDoesNotReplaceIngress()
    {
        var reader = new RecordingReader(CreateWebService());
        var ingressLabels = new WorkloadIdentityLabels(
            TenantId,
            ResourceGroupId,
            IsolationScopeId,
            RuntimeCellId,
            TargetId,
            "WebService",
            ResourceId,
            DeploymentId).ToDictionary();
        var ingressJson = JsonSerializer.Serialize(new
        {
            metadata = new
            {
                uid = "ingress-uid",
                resourceVersion = "7",
                labels = ingressLabels,
                annotations = new Dictionary<string, string>
                {
                    ["pole.server/deploymentGeneration"] = "1"
                }
            }
        });
        using var handler = new RecordingKubernetesHandler
        {
            ConflictIngressCreate = true,
            BlockIngressRead = true,
            IngressReadResponse = ingressJson
        };
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);
        var subject = new WebServiceResourceDeploymentAdapter(
            reader.LoadSnapshot,
            reader.LoadById,
            reader.LoadExactDeployment,
            UnexpectedRegistryAccess,
            new FixedKubernetesClientProvider(kubernetes),
            SuccessfulPolicy,
            EmptyConnectionReferences,
            EmptySecretReferences);

        var applyTask = subject.ApplyAsync(CreateContext(includeIngress: true));
        await handler.IngressReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        reader.CurrentGeneration = 2;
        handler.ContinueIngressRead.TrySetResult();
        var result = await applyTask;

        var failed = Assert.IsType<ResourceDeploymentAdapterResult.Failed>(result);
        Assert.Equal("DeploymentGenerationSuperseded", failed.FailureCode);
        Assert.DoesNotContain(handler.Requests, request =>
            request.Method == HttpMethod.Put &&
            request.Path.EndsWith("/ingresses/api-ing", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_PublicGenerationSupersededAfterIngressWriteDeletesOnlyReturnedIdentity(
        bool replace)
    {
        var reader = new RecordingReader(CreateWebService());
        using var handler = new RecordingKubernetesHandler
        {
            ConflictIngressCreate = replace,
            IngressReadResponse = CurrentIngressJson(),
            IngressWriteResponse = CurrentIngressJson(resourceVersion: "8"),
            BlockIngressWriteResponse = true,
            CurrentIngressUid = "ingress-uid",
            CurrentIngressResourceVersion = "8"
        };
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);
        var subject = new WebServiceResourceDeploymentAdapter(
            reader.LoadSnapshot,
            reader.LoadById,
            reader.LoadExactDeployment,
            UnexpectedRegistryAccess,
            new FixedKubernetesClientProvider(kubernetes),
            SuccessfulPolicy,
            EmptyConnectionReferences,
            EmptySecretReferences);

        var applyTask = subject.ApplyAsync(CreateContext(includeIngress: true));
        await handler.IngressWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        reader.CurrentGeneration = 2;
        handler.ContinueIngressWrite.TrySetResult();
        var result = await applyTask;

        var failed = Assert.IsType<ResourceDeploymentAdapterResult.Failed>(result);
        Assert.Equal("DeploymentGenerationSuperseded", failed.FailureCode);
        var deletion = Assert.Single(handler.Requests, request =>
            request.Method == HttpMethod.Delete &&
            request.Path.EndsWith("/ingresses/api-ing", StringComparison.Ordinal));
        AssertIngressDeletePreconditions(deletion, "ingress-uid", "8");
        Assert.True(handler.IngressDeleted);
        Assert.Equal(HttpStatusCode.OK, handler.IngressDeleteResponseStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_PublicStaleWriteDoesNotDeleteChangedOrRemovedIngress(
        bool removedBeforeCompensation)
    {
        var reader = new RecordingReader(CreateWebService());
        using var handler = new RecordingKubernetesHandler
        {
            IngressWriteResponse = CurrentIngressJson(resourceVersion: "8"),
            BlockIngressWriteResponse = true,
            IngressMissingOnDelete = removedBeforeCompensation,
            CurrentIngressUid = "ingress-uid",
            CurrentIngressResourceVersion = "9"
        };
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);
        var subject = new WebServiceResourceDeploymentAdapter(
            reader.LoadSnapshot,
            reader.LoadById,
            reader.LoadExactDeployment,
            UnexpectedRegistryAccess,
            new FixedKubernetesClientProvider(kubernetes),
            SuccessfulPolicy,
            EmptyConnectionReferences,
            EmptySecretReferences);

        var applyTask = subject.ApplyAsync(CreateContext(includeIngress: true));
        await handler.IngressWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        reader.CurrentGeneration = 2;
        handler.ContinueIngressWrite.TrySetResult();
        var result = await applyTask;

        var failed = Assert.IsType<ResourceDeploymentAdapterResult.Failed>(result);
        Assert.Equal("DeploymentGenerationSuperseded", failed.FailureCode);
        var deletion = Assert.Single(handler.Requests, request =>
            request.Method == HttpMethod.Delete &&
            request.Path.EndsWith("/ingresses/api-ing", StringComparison.Ordinal));
        AssertIngressDeletePreconditions(deletion, "ingress-uid", "8");
        Assert.False(handler.IngressDeleted);
        Assert.Equal(
            removedBeforeCompensation ? HttpStatusCode.NotFound : HttpStatusCode.Conflict,
            handler.IngressDeleteResponseStatus);
    }

    [Fact]
    public async Task Apply_PublicStaleWriteWithoutReturnedIdentityNeverDeletesIngress()
    {
        var reader = new RecordingReader(CreateWebService());
        using var handler = new RecordingKubernetesHandler
        {
            IngressWriteResponse = JsonSerializer.Serialize(new { metadata = new { name = "api-ing" } }),
            BlockIngressWriteResponse = true
        };
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);
        var subject = new WebServiceResourceDeploymentAdapter(
            reader.LoadSnapshot,
            reader.LoadById,
            reader.LoadExactDeployment,
            UnexpectedRegistryAccess,
            new FixedKubernetesClientProvider(kubernetes),
            SuccessfulPolicy,
            EmptyConnectionReferences,
            EmptySecretReferences);

        var applyTask = subject.ApplyAsync(CreateContext(includeIngress: true));
        await handler.IngressWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        reader.CurrentGeneration = 2;
        handler.ContinueIngressWrite.TrySetResult();
        var result = await applyTask;

        var failed = Assert.IsType<ResourceDeploymentAdapterResult.Failed>(result);
        Assert.Equal("DeploymentGenerationSuperseded", failed.FailureCode);
        Assert.DoesNotContain(handler.Requests, request =>
            request.Method == HttpMethod.Delete &&
            request.Path.EndsWith("/ingresses/api-ing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Apply_PublicPolicySourceFailureStillAttemptsHttp01RevocationBeforeWorkloadApply()
    {
        var webService = CreateWebService();
        var policyRequests = new List<ResourceIngressPolicyRequest>();
        Task<ReconcileResourceIngressPolicyOutcome> ApplyPolicy(
            ResourceIngressPolicyRequest request,
            CancellationToken cancellationToken = default)
        {
            policyRequests.Add(request);
            return Task.FromResult<ReconcileResourceIngressPolicyOutcome>(
                request.Purpose is "web-public"
                    ? new ResourceIngressPolicySourceUnavailable()
                    : new ResourceIngressPolicyApplied());
        }
        var reader = new RecordingReader(webService);
        using var handler = new RecordingKubernetesHandler
        {
            IngressReadResponse = CurrentIngressJson()
        };
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);
        var subject = new WebServiceResourceDeploymentAdapter(
            reader.LoadSnapshot,
            reader.LoadById,
            reader.LoadExactDeployment,
            UnexpectedRegistryAccess,
            new FixedKubernetesClientProvider(kubernetes),
            ApplyPolicy,
            EmptyConnectionReferences,
            EmptySecretReferences);

        var result = await subject.ApplyAsync(CreateContext(includeIngress: true));

        var failed = Assert.IsType<ResourceDeploymentAdapterResult.Failed>(result);
        Assert.Equal("PublicIngressSourceUnavailable", failed.FailureCode);
        Assert.Equal(2, policyRequests.Count);
        Assert.Equal("web-http01", policyRequests[1].Purpose);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Apply_PrivateRevokesPublicExposureBeforeRegistryCredentialFailure()
    {
        var webService = CreateWebService() with
        {
            Exposure = WebServiceExposure.Create(WebServiceExposureMode.Private),
            Spec = CreateWebService().Spec with
            {
                Image = new ImageInfo("registry.example.test", "pole/api", "1.0", "registry-pull")
            }
        };
        var policyRequests = new List<ResourceIngressPolicyRequest>();
        Task<ReconcileResourceIngressPolicyOutcome> ApplyPolicy(
            ResourceIngressPolicyRequest request,
            CancellationToken cancellationToken = default)
        {
            policyRequests.Add(request);
            return Task.FromResult<ReconcileResourceIngressPolicyOutcome>(new ResourceIngressPolicyApplied());
        }
        var reader = new RecordingReader(webService);
        using var handler = new RecordingKubernetesHandler
        {
            IngressReadResponse = CurrentIngressJson()
        };
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);
        var subject = new WebServiceResourceDeploymentAdapter(
            reader.LoadSnapshot,
            reader.LoadById,
            reader.LoadExactDeployment,
            UnavailableRegistryAccess,
            new FixedKubernetesClientProvider(kubernetes),
            ApplyPolicy,
            EmptyConnectionReferences,
            EmptySecretReferences);

        var result = await subject.ApplyAsync(CreateContext(includeIngress: false));

        var failed = Assert.IsType<ResourceDeploymentAdapterResult.Failed>(result);
        Assert.Equal("RegistryCredentialUnavailable", failed.FailureCode);
        Assert.Equal(2, policyRequests.Count);
        Assert.All(policyRequests, request => Assert.Empty(request.AllowedTcpPorts));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Delete &&
            request.Path.EndsWith($"/ingresses/{WebServiceInKubernetes.BuildIngressName(webService.Name)}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_UsesOnlyTheContextClientAndPersistedNamespace()
    {
        var reader = new RecordingReader(CreateWebService());
        using var handler = new RecordingKubernetesHandler();
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);
        var subject = new WebServiceResourceDeploymentAdapter(
            reader.LoadSnapshot,
            reader.LoadById,
            reader.LoadExactDeployment,
            UnexpectedRegistryAccess,
            new FixedKubernetesClientProvider(kubernetes),
            SuccessfulPolicy,
            EmptyConnectionReferences,
            EmptySecretReferences);

        var result = await subject.DeleteAsync(
            CreateContext(includeIngress: true));

        var succeeded = Assert.IsType<ResourceDeploymentAdapterResult.Succeeded>(result);
        Assert.Empty(succeeded.Endpoints);
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal([(TenantId, ResourceId)], reader.DeleteRequests);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Contains(
                $"/namespaces/{KubernetesNamespace}/",
                request.Path,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Delete_AttemptsBothPolicyClearsWhenTheFirstWriteFails()
    {
        var policyRequests = new List<ResourceIngressPolicyRequest>();
        Task<ReconcileResourceIngressPolicyOutcome> ApplyPolicy(
            ResourceIngressPolicyRequest request,
            CancellationToken cancellationToken = default)
        {
            policyRequests.Add(request);
            return Task.FromResult<ReconcileResourceIngressPolicyOutcome>(
                request.Purpose is "web-public"
                    ? new ResourceIngressPolicyUnavailable()
                    : new ResourceIngressPolicyApplied());
        }
        var reader = new RecordingReader(CreateWebService());
        using var handler = new RecordingKubernetesHandler();
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);
        var subject = new WebServiceResourceDeploymentAdapter(
            reader.LoadSnapshot,
            reader.LoadById,
            reader.LoadExactDeployment,
            UnexpectedRegistryAccess,
            new FixedKubernetesClientProvider(kubernetes),
            ApplyPolicy,
            EmptyConnectionReferences,
            EmptySecretReferences);

        var result = await subject.DeleteAsync(CreateContext(includeIngress: true));

        var failed = Assert.IsType<ResourceDeploymentAdapterResult.Failed>(result);
        Assert.Equal("IngressPolicyReconciliationFailed", failed.FailureCode);
        Assert.Equal(
            ["web-public", "web-http01"],
            policyRequests.Select(request => request.Purpose));
    }

    [Fact]
    public async Task Delete_MissingLogicalResourceStillWritesPolicyTombstones()
    {
        var reader = new RecordingReader(CreateWebService()) { MissingLogicalResource = true };
        var policyRequests = new List<ResourceIngressPolicyRequest>();
        Task<ReconcileResourceIngressPolicyOutcome> ApplyPolicy(
            ResourceIngressPolicyRequest request,
            CancellationToken cancellationToken = default)
        {
            policyRequests.Add(request);
            return Task.FromResult<ReconcileResourceIngressPolicyOutcome>(
                request.Purpose is "web-public"
                    ? new ResourceIngressPolicySourceUnavailable()
                    : new ResourceIngressPolicyApplied());
        }
        using var handler = new RecordingKubernetesHandler();
        using var kubernetes = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);
        var subject = new WebServiceResourceDeploymentAdapter(
            reader.LoadSnapshot,
            reader.LoadById,
            reader.LoadExactDeployment,
            UnexpectedRegistryAccess,
            new FixedKubernetesClientProvider(kubernetes),
            ApplyPolicy,
            EmptyConnectionReferences,
            EmptySecretReferences);

        var result = await subject.DeleteAsync(CreateContext(includeIngress: false));

        var failed = Assert.IsType<ResourceDeploymentAdapterResult.Failed>(result);
        Assert.Equal("PublicIngressSourceUnavailable", failed.FailureCode);
        Assert.True(failed.Retryable);
        Assert.Equal(
            ["web-public", "web-http01"],
            policyRequests.Select(request => request.Purpose));
        Assert.All(policyRequests, request => Assert.Empty(request.AllowedTcpPorts));
        Assert.Empty(handler.Requests);
        Assert.Empty(reader.DeleteRequests);
    }

    private static ResourceDeploymentReconciliationContext CreateContext(
        bool includeIngress)
    {
        var placement = new ResourceDeploymentResolvedPlacementSnapshot(
            TargetId,
            TargetWorkloadConfigurationRevision: 1,
            KubernetesNamespace,
            includeIngress
                ? new ResourceDeploymentIngressConfiguration(
                    "nginx-private",
                    "pole-private-issuer",
                    "example.test",
                    ["api.example.test", "www.api.example.test"])
                : null,
            Storage: null,
            ResourceDeploymentTestData.ContractCompute(TargetId));
        var deployment = new ResourceDeploymentReconciliationSnapshot(
            DeploymentId,
            TenantId,
            WebServiceResourceDeployment.ResourceType,
            ResourceId,
            ResourceGroupId,
            IsolationScopeId,
            RuntimeCellId,
            ResourceRevision: 1,
            FixedReplicas: 2,
            DesiredGeneration: 1,
            UpdatedAtUtc: NowUtc,
            Placement: placement);
        return new ResourceDeploymentReconciliationContext(deployment);
    }

    private static WebService CreateWebService(
        string targetPort = "8080",
        string protocol = "TCP") =>
        WebServiceFactory.Create(
            name: "api",
            tenantId: TenantId,
            resourceGroupId: ResourceGroupId,
            isolationScopeId: IsolationScopeId,
            spec: new WebServiceSpec(
                new ImageInfo("registry.example.test", "pole/api", "1.0"),
                Config: new WebServiceConfig(
                    ImmutableDictionary<string, string>.Empty,
                    [new PortMapping(80, targetPort, protocol, "http")]),
                Autoscaling: null,
                Probes: new ProbeSpec(
                    new ProbeConfig("/health", "8080"),
                    new ProbeConfig("/health", "8080"),
                    new ProbeConfig("/health", "8080"))),
            exposure: WebServiceExposure.Create(WebServiceExposureMode.Public),
            options: WebServiceFactory.DefaultOptions with
            {
                Id = ResourceId
            });

    private static void AssertIngressDeletePreconditions(
        RecordedRequest request,
        string uid,
        string resourceVersion)
    {
        using var body = JsonDocument.Parse(request.Body);
        var preconditions = body.RootElement.GetProperty("preconditions");
        Assert.Equal(uid, preconditions.GetProperty("uid").GetString());
        Assert.Equal(resourceVersion, preconditions.GetProperty("resourceVersion").GetString());
    }

    private static string CurrentIngressJson(
        string uid = "ingress-uid",
        string resourceVersion = "7") => JsonSerializer.Serialize(new
        {
            metadata = new
            {
                name = "api-ing",
                @namespace = KubernetesNamespace,
                uid,
                resourceVersion,
                labels = new WorkloadIdentityLabels(
                    TenantId,
                    ResourceGroupId,
                    IsolationScopeId,
                    RuntimeCellId,
                    TargetId,
                    "WebService",
                    ResourceId,
                    DeploymentId).ToDictionary(),
                annotations = new Dictionary<string, string>
                {
                    ["pole.server/deploymentGeneration"] = "1"
                }
            }
        });

    private sealed class RecordingReader(WebService webService)
    {
        public bool MissingLogicalResource { get; init; }
        public long CurrentGeneration { get; set; } = 1;
        public List<(string TenantId, string ResourceId, string DeploymentId, long ResourceRevision, long Generation)>
            ApplyRequests
        { get; } = [];
        public List<(string TenantId, string ResourceId)> DeleteRequests { get; } = [];

        public Task<WebServiceDeploymentPersistenceSnapshot?> LoadSnapshot(
            string tenantId,
            string webServiceId,
            string resourceDeploymentId,
            long resourceRevision,
            long deploymentGeneration,
            CancellationToken cancellationToken = default)
        {
            ApplyRequests.Add((tenantId, webServiceId, resourceDeploymentId, resourceRevision, deploymentGeneration));
            return Task.FromResult<WebServiceDeploymentPersistenceSnapshot?>(
                new WebServiceDeploymentPersistenceSnapshot(
                    webService,
                    ReleaseId,
                    webService.ActiveRevision,
                    resourceDeploymentId,
                    deploymentGeneration,
                    NowUtc));
        }

        public Task<WebService?> LoadById(
            string tenantId,
            string webServiceId,
            CancellationToken cancellationToken = default)
        {
            DeleteRequests.Add((tenantId, webServiceId));
            return Task.FromResult<WebService?>(MissingLogicalResource ? null : webService);
        }

        public Task<PlacementSummaryDto?> LoadExactDeployment(
            string tenantId,
            Pole.Workloads.ResourceTypeId resourceType,
            string resourceId,
            string deploymentId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PlacementSummaryDto?>(new PlacementSummaryDto(
                DeploymentId,
                IsolationScopeId,
                RuntimeCellId,
                TargetId,
                DesiredGeneration: CurrentGeneration,
                ObservedGeneration: 0,
                Status: "Pending",
                FailureCode: null,
                Compute: new ComputeConfigurationSummaryDto(
                    new ComputeProfileReference("profile-1", 1),
                    "test",
                    new ComputeResourceValuesDto(100, 128),
                    new ComputeResourceValuesDto(100, 128),
                    []),
                Endpoints: []));
    }

    private static Task<ResolveRuntimeRegistryAccessOutcome>
        UnexpectedRegistryAccess(
            string tenantId,
            CancellationToken cancellationToken = default) =>
        throw new Xunit.Sdk.XunitException(
            "An image without a pull secret must not request registry credentials.");

    private static Task<ResolveRuntimeRegistryAccessOutcome> UnavailableRegistryAccess(
        string tenantId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ResolveRuntimeRegistryAccessOutcome>(
            new ResolveRuntimeRegistryAccessError(
                new TenantContainerRegistryNotReady(tenantId)));

    private static Task<ReconcileResourceIngressPolicyOutcome> SuccessfulPolicy(
        ResourceIngressPolicyRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ReconcileResourceIngressPolicyOutcome>(new ResourceIngressPolicyApplied());

    private sealed class RecordingKubernetesHandler : DelegatingHandler
    {
        private bool _ingressDeleted;
        public List<RecordedRequest> Requests { get; } = [];
        public bool ConflictIngressCreate { get; init; }
        public bool BlockIngressRead { get; init; }
        public bool BlockIngressWriteResponse { get; init; }
        public string? IngressReadResponse { get; init; }
        public string? IngressWriteResponse { get; init; }
        public bool IngressMissingOnDelete { get; init; }
        public string? CurrentIngressUid { get; set; }
        public string? CurrentIngressResourceVersion { get; set; }
        public bool IngressDeleted => _ingressDeleted;
        public HttpStatusCode? IngressDeleteResponseStatus { get; private set; }
        public TaskCompletionSource IngressReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueIngressRead { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource IngressWriteStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueIngressWrite { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? "{}"
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.AbsolutePath,
                body));
            if (ConflictIngressCreate && request.Method == HttpMethod.Post &&
                request.RequestUri.AbsolutePath.EndsWith("/ingresses", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    RequestMessage = request,
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
            }
            var ingressWrite =
                (request.Method == HttpMethod.Post &&
                 request.RequestUri.AbsolutePath.EndsWith("/ingresses", StringComparison.Ordinal)) ||
                (request.Method == HttpMethod.Put &&
                 request.RequestUri.AbsolutePath.EndsWith("/ingresses/api-ing", StringComparison.Ordinal));
            if (ingressWrite && IngressWriteResponse is not null)
            {
                IngressWriteStarted.TrySetResult();
                if (BlockIngressWriteResponse)
                    await ContinueIngressWrite.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(
                    request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(IngressWriteResponse, Encoding.UTF8, "application/json")
                };
            }
            if (request.Method == HttpMethod.Get &&
                request.RequestUri.AbsolutePath.EndsWith("/ingresses/api-ing", StringComparison.Ordinal))
            {
                IngressReadStarted.TrySetResult();
                if (BlockIngressRead)
                    await ContinueIngressRead.Task.WaitAsync(cancellationToken);
                if (_ingressDeleted || IngressReadResponse is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        RequestMessage = request,
                        Content = new StringContent("{}", Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(IngressReadResponse ?? "{}", Encoding.UTF8, "application/json")
                };
            }
            if (request.Method == HttpMethod.Delete &&
                request.RequestUri.AbsolutePath.EndsWith("/ingresses/api-ing", StringComparison.Ordinal))
            {
                if (IngressMissingOnDelete)
                {
                    IngressDeleteResponseStatus = HttpStatusCode.NotFound;
                    return new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        RequestMessage = request,
                        Content = new StringContent("{}", Encoding.UTF8, "application/json")
                    };
                }
                if (CurrentIngressUid is not null && CurrentIngressResourceVersion is not null)
                {
                    using var deletion = JsonDocument.Parse(body);
                    var preconditions = deletion.RootElement.GetProperty("preconditions");
                    if (preconditions.GetProperty("uid").GetString() != CurrentIngressUid ||
                        preconditions.GetProperty("resourceVersion").GetString() != CurrentIngressResourceVersion)
                    {
                        IngressDeleteResponseStatus = HttpStatusCode.Conflict;
                        return new HttpResponseMessage(HttpStatusCode.Conflict)
                        {
                            RequestMessage = request,
                            Content = new StringContent("{}", Encoding.UTF8, "application/json")
                        };
                    }
                }
                _ingressDeleted = true;
                IngressDeleteResponseStatus = HttpStatusCode.OK;
            }
            var absentIngressRead = request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath.Contains("/ingresses/", StringComparison.Ordinal);
            return new HttpResponseMessage(
                absentIngressRead
                    ? HttpStatusCode.NotFound
                    : request.Method == HttpMethod.Delete
                    ? HttpStatusCode.OK
                    : HttpStatusCode.Created)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        string Body);

    private const string TenantId = "507f1f77bcf86cd799439011";
    private const string ResourceId = "507f1f77bcf86cd799439012";
    private const string ResourceGroupId = "507f1f77bcf86cd799439013";
    private const string IsolationScopeId = "507f1f77bcf86cd799439014";
    private const string RuntimeCellId = "507f1f77bcf86cd799439015";
    private const string TargetId = "507f1f77bcf86cd799439016";
    private const string DeploymentId = "507f1f77bcf86cd799439017";
    private const string ReleaseId = "507f1f77bcf86cd799439018";
    private const string KubernetesNamespace = "exact-target-namespace";
    private static readonly DateTime NowUtc =
        new(2026, 7, 29, 14, 0, 0, DateTimeKind.Utc);
}
