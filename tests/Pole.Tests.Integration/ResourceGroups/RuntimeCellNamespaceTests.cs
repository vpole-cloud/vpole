using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceGroups.Application;
using Pole.Features.ResourceGroups.Application.Kubernetes;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;
using Pole.Server.Extensions;
using Pole.Platform.Hosting;
using Xunit;

namespace Pole.Tests.Integration.ResourceGroups;

public sealed class RuntimeCellNamespaceTests
{
    [Fact]
    public void NameGenerator_NormalizesAndBoundsPrefix_AndIsStableForScope()
    {
        var subject = new RuntimeCellNamespaceNameGenerator(new KubernetesOptions
        {
            NamespaceNamePrefix = " --POLE__Control-Plane-With-A-Very-Long-Prefix!!-- "
        });

        var first = subject.Invoke(TenantId, ScopeId);
        var repeated = subject.Invoke(TenantId, ScopeId);

        Assert.Equal(first, repeated);
        Assert.Equal("polecontrol-plane-with-a-t-507f1f77-is-507f1f77bcf86cd799439013", first);
        Assert.Equal(63, first.Length);
        Assert.Matches("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", first);
    }

    [Fact]
    public void NameGenerator_UsesFullScopeId_AndSeparatesScopes()
    {
        var subject = new RuntimeCellNamespaceNameGenerator(new KubernetesOptions
        {
            NamespaceNamePrefix = "pole"
        });
        const string otherScopeId = "507f1f77bcf86cd799439099";

        var first = subject.Invoke(TenantId, ScopeId);
        var second = subject.Invoke(TenantId, otherScopeId);

        Assert.EndsWith(ScopeId, first, StringComparison.Ordinal);
        Assert.EndsWith(otherScopeId, second, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("")]
    [InlineData("---")]
    [InlineData("___!!!")]
    public void NameGenerator_RejectsPrefixWithoutDnsLabelContent(string prefix)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new RuntimeCellNamespaceNameGenerator(new KubernetesOptions
            {
                NamespaceNamePrefix = prefix
            }));

        Assert.DoesNotContain("token", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidNamespacePrefix_PreventsHostStartup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["kubernetes:NamespaceNamePrefix"] = "---"
            })
            .Build();
        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddResourceGroupsFeature(configuration))
            .Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Fact]
    public void ManifestBuilder_ProducesNamespaceWithCompleteOwnershipLabels()
    {
        var runtimeCell = CreateRuntimeCell();

        var manifest = RuntimeCellNamespaceManifestBuilder.Build(runtimeCell);

        Assert.Equal("v1", manifest.ApiVersion);
        Assert.Equal("Namespace", manifest.Kind);
        Assert.Equal(runtimeCell.KubernetesNamespace, manifest.Metadata.Name);
        Assert.Equal(5, manifest.Metadata.Labels.Count);
        Assert.Equal(runtimeCell.TenantId, manifest.Metadata.Labels[RuntimeCellNamespaceLabels.TenantId]);
        Assert.Equal(runtimeCell.ResourceGroupId, manifest.Metadata.Labels[RuntimeCellNamespaceLabels.ResourceGroupId]);
        Assert.Equal(runtimeCell.IsolationScopeId, manifest.Metadata.Labels[RuntimeCellNamespaceLabels.IsolationScopeId]);
        Assert.Equal(runtimeCell.Id, manifest.Metadata.Labels[RuntimeCellNamespaceLabels.RuntimeCellId]);
        Assert.Equal(runtimeCell.DeploymentTargetId, manifest.Metadata.Labels[RuntimeCellNamespaceLabels.DeploymentTargetId]);
    }

    [Fact]
    public void ManifestBuilder_AddsValidatedExternalLabelsWithoutReplacingOwnership()
    {
        var runtimeCell = CreateRuntimeCell();

        var manifest = RuntimeCellNamespaceManifestBuilder.Build(
            runtimeCell,
            new Dictionary<string, string>
            {
                ["pole.dev/owned-by"] = "pole-tests",
                ["pole.dev/test-run-id"] = "run-123"
            });

        Assert.Equal(7, manifest.Metadata.Labels.Count);
        Assert.Equal("pole-tests", manifest.Metadata.Labels["pole.dev/owned-by"]);
        Assert.Equal("run-123", manifest.Metadata.Labels["pole.dev/test-run-id"]);
        Assert.Equal(runtimeCell.Id, manifest.Metadata.Labels[RuntimeCellNamespaceLabels.RuntimeCellId]);
    }

    [Theory]
    [InlineData("not a label", "value")]
    [InlineData("pole.dev/example", "not a value!")]
    [InlineData(RuntimeCellNamespaceLabels.RuntimeCellId, "replacement")]
    public async Task InvalidAdditionalNamespaceLabels_PreventHostStartup(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["kubernetes:NamespaceNamePrefix"] = "pole",
                [$"kubernetes:AdditionalNamespaceLabels:{key}"] = value
            })
            .Build();
        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddResourceGroupsFeature(configuration))
            .Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Fact]
    public async Task EnsureCreated_AppliesConfiguredAdditionalNamespaceLabels()
    {
        var runtimeCell = CreateRuntimeCell();
        using var handler = new NamespaceApiHandler();
        using var kubernetes = CreateKubernetes(handler);
        var options = new KubernetesOptions
        {
            NamespaceNamePrefix = "pole",
            AdditionalNamespaceLabels = new Dictionary<string, string>
            {
                ["pole.dev/owned-by"] = "pole-tests"
            }
        };
        var subject = new KubernetesRuntimeCellNamespaceManager(
            new RecordingKubernetesClientProvider(kubernetes),
            options);

        var result = await subject.EnsureCreatedAsync(runtimeCell);

        Assert.True(result is Unit);
        Assert.Equal("pole-tests", handler.CreatedNamespace!.Metadata.Labels["pole.dev/owned-by"]);
    }

    [Fact]
    public async Task EnsureCreated_UsesPersistedTarget_AndCreatesDesiredManifest()
    {
        var runtimeCell = CreateRuntimeCell();
        using var handler = new NamespaceApiHandler();
        using var kubernetes = CreateKubernetes(handler);
        var provider = new RecordingKubernetesClientProvider(kubernetes);
        var subject = new KubernetesRuntimeCellNamespaceManager(provider);

        var result = await subject.EnsureCreatedAsync(runtimeCell);

        Assert.True(result is Unit);
        Assert.Equal([runtimeCell.DeploymentTargetId], provider.RequestedTargetIds);
        Assert.Equal(1, handler.CreateRequests);
        Assert.NotNull(handler.CreatedNamespace);
        Assert.Equal(runtimeCell.KubernetesNamespace, handler.CreatedNamespace.Metadata.Name);
        Assert.Equal(runtimeCell.Id, handler.CreatedNamespace.Metadata.Labels[RuntimeCellNamespaceLabels.RuntimeCellId]);
    }

    [Fact]
    public async Task EnsureCreated_ExistingOwnedNamespace_IsIdempotent()
    {
        var runtimeCell = CreateRuntimeCell();
        using var handler = new NamespaceApiHandler
        {
            CreateStatusCode = HttpStatusCode.Conflict,
            ExistingNamespace = RuntimeCellNamespaceManifestBuilder.Build(runtimeCell)
        };
        using var kubernetes = CreateKubernetes(handler);
        var subject = new KubernetesRuntimeCellNamespaceManager(
            new RecordingKubernetesClientProvider(kubernetes));

        var result = await subject.EnsureCreatedAsync(runtimeCell);

        Assert.True(result is Unit);
        Assert.Equal(1, handler.CreateRequests);
        Assert.Equal(1, handler.ReadRequests);
    }

    [Fact]
    public async Task EnsureCreated_ExistingNamespaceWithoutMatchingOwnership_ReturnsConflict()
    {
        var runtimeCell = CreateRuntimeCell();
        var existing = RuntimeCellNamespaceManifestBuilder.Build(runtimeCell);
        existing.Metadata.Labels.Remove(RuntimeCellNamespaceLabels.RuntimeCellId);
        using var handler = new NamespaceApiHandler
        {
            CreateStatusCode = HttpStatusCode.Conflict,
            ExistingNamespace = existing
        };
        using var kubernetes = CreateKubernetes(handler);
        var subject = new KubernetesRuntimeCellNamespaceManager(
            new RecordingKubernetesClientProvider(kubernetes));

        var result = await subject.EnsureCreatedAsync(runtimeCell);

        Assert.True(result is RuntimeCellNamespaceOwnershipConflict);
    }

    [Fact]
    public async Task Delete_MissingNamespace_IsIdempotent()
    {
        var runtimeCell = CreateRuntimeCell();
        using var handler = new NamespaceApiHandler
        {
            ReadStatusCode = HttpStatusCode.NotFound
        };
        using var kubernetes = CreateKubernetes(handler);
        var subject = new KubernetesRuntimeCellNamespaceManager(
            new RecordingKubernetesClientProvider(kubernetes));

        var result = await subject.DeleteAsync(runtimeCell);

        Assert.True(result is Unit);
        Assert.Equal(1, handler.ReadRequests);
        Assert.Equal(0, handler.DeleteRequests);
    }

    [Fact]
    public async Task Delete_OwnedNamespace_TreatsDeleteNotFoundAsSuccess()
    {
        var runtimeCell = CreateRuntimeCell();
        using var handler = new NamespaceApiHandler
        {
            ExistingNamespace = RuntimeCellNamespaceManifestBuilder.Build(runtimeCell),
            DeleteStatusCode = HttpStatusCode.NotFound
        };
        using var kubernetes = CreateKubernetes(handler);
        var provider = new RecordingKubernetesClientProvider(kubernetes);
        var subject = new KubernetesRuntimeCellNamespaceManager(provider);

        var result = await subject.DeleteAsync(runtimeCell);

        Assert.True(result is Unit);
        Assert.Equal([runtimeCell.DeploymentTargetId], provider.RequestedTargetIds);
        Assert.Equal(1, handler.DeleteRequests);
    }

    [Fact]
    public async Task Delete_OwnedNamespace_WaitsForConfirmedNotFound()
    {
        var runtimeCell = CreateRuntimeCell();
        using var handler = new NamespaceApiHandler
        {
            ExistingNamespace = RuntimeCellNamespaceManifestBuilder.Build(runtimeCell),
            ReadStatusCodes = [HttpStatusCode.OK, HttpStatusCode.NotFound]
        };
        using var kubernetes = CreateKubernetes(handler);
        var subject = new KubernetesRuntimeCellNamespaceManager(
            new RecordingKubernetesClientProvider(kubernetes));

        var result = await subject.DeleteAsync(runtimeCell);

        Assert.True(result is Unit);
        Assert.Equal(2, handler.ReadRequests);
        Assert.Equal(1, handler.DeleteRequests);
    }

    [Fact]
    public async Task Delete_OwnedNamespace_StillPresent_ReturnsRetryableFailure()
    {
        var runtimeCell = CreateRuntimeCell();
        using var handler = new NamespaceApiHandler
        {
            ExistingNamespace = RuntimeCellNamespaceManifestBuilder.Build(runtimeCell),
            ReadStatusCodes = [HttpStatusCode.OK, HttpStatusCode.OK]
        };
        using var kubernetes = CreateKubernetes(handler);
        var subject = new KubernetesRuntimeCellNamespaceManager(
            new RecordingKubernetesClientProvider(kubernetes));

        var result = await subject.DeleteAsync(runtimeCell);

        Assert.True(result is RuntimeCellNamespaceUnavailable { DeletionPending: true });
        Assert.Equal(2, handler.ReadRequests);
        Assert.Equal(1, handler.DeleteRequests);
    }

    [Fact]
    public async Task Delete_ForeignNamespace_ReturnsConflictWithoutDeleting()
    {
        var runtimeCell = CreateRuntimeCell();
        var existing = RuntimeCellNamespaceManifestBuilder.Build(runtimeCell);
        existing.Metadata.Labels[RuntimeCellNamespaceLabels.DeploymentTargetId] =
            "507f1f77bcf86cd799439099";
        using var handler = new NamespaceApiHandler
        {
            ExistingNamespace = existing
        };
        using var kubernetes = CreateKubernetes(handler);
        var subject = new KubernetesRuntimeCellNamespaceManager(
            new RecordingKubernetesClientProvider(kubernetes));

        var result = await subject.DeleteAsync(runtimeCell);

        Assert.True(result is RuntimeCellNamespaceOwnershipConflict);
        Assert.Equal(0, handler.DeleteRequests);
    }

    [Fact]
    public async Task KubernetesApiFailure_ReturnsSanitizedFailure()
    {
        using var handler = new NamespaceApiHandler
        {
            CreateStatusCode = HttpStatusCode.InternalServerError
        };
        using var kubernetes = CreateKubernetes(handler);
        var subject = new KubernetesRuntimeCellNamespaceManager(
            new RecordingKubernetesClientProvider(kubernetes));

        var result = await subject.EnsureCreatedAsync(CreateRuntimeCell());

        var unavailable = result switch
        {
            RuntimeCellNamespaceUnavailable failure => failure,
            RuntimeCellNamespaceOwnershipConflict => throw new Xunit.Sdk.XunitException(
                "Expected a sanitized namespace-provider failure, got an ownership conflict."),
            Unit => throw new Xunit.Sdk.XunitException(
                "Expected a sanitized namespace-provider failure, got success.")
        };
        Assert.DoesNotContain("provider-secret-token", unavailable.KubernetesNamespace, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedProviderFailure_IsNotMasked()
    {
        var subject = new KubernetesRuntimeCellNamespaceManager(
            new ThrowingKubernetesClientProvider(
                new InvalidOperationException("unexpected-provider-bug")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => subject.EnsureCreatedAsync(CreateRuntimeCell()));

        Assert.Equal("unexpected-provider-bug", error.Message);
    }

    private static Kubernetes CreateKubernetes(DelegatingHandler handler) =>
        new(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);

    private static RuntimeCell CreateRuntimeCell()
    {
        var namespaceName = new RuntimeCellNamespaceNameGenerator(new KubernetesOptions
        {
            NamespaceNamePrefix = "pole"
        }).Invoke(TenantId, ScopeId);
        return RuntimeCellFactory.Create(
            TenantId,
            ResourceGroupId,
            ScopeId,
            DeploymentTargetId,
            namespaceName,
            new RuntimeCellFactory.CreateOptions(
                RuntimeCellId,
                new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc)));
    }

    private const string TenantId = "507f1f77bcf86cd799439011";
    private const string ResourceGroupId = "507f1f77bcf86cd799439012";
    private const string ScopeId = "507f1f77bcf86cd799439013";
    private const string DeploymentTargetId = "507f1f77bcf86cd799439014";
    private const string RuntimeCellId = "507f1f77bcf86cd799439015";

    private sealed class RecordingKubernetesClientProvider(IKubernetes kubernetes)
        : IKubernetesClientProvider
    {
        public List<string> RequestedTargetIds { get; } = [];

        public Task<IKubernetes> GetAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default)
        {
            RequestedTargetIds.Add(deploymentTargetId);
            return Task.FromResult(kubernetes);
        }

        public Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The default Kubernetes client must not be used.");
    }

    private sealed class ThrowingKubernetesClientProvider(Exception exception)
        : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<IKubernetes>(exception);

        public Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<IKubernetes>(exception);
    }

    private sealed class NamespaceApiHandler : DelegatingHandler
    {
        public HttpStatusCode CreateStatusCode { get; init; } = HttpStatusCode.Created;
        public HttpStatusCode ReadStatusCode { get; init; } = HttpStatusCode.OK;
        public IReadOnlyList<HttpStatusCode>? ReadStatusCodes { get; init; }
        public HttpStatusCode DeleteStatusCode { get; init; } = HttpStatusCode.OK;
        public V1Namespace? ExistingNamespace { get; init; }
        public V1Namespace? CreatedNamespace { get; private set; }
        public int CreateRequests { get; private set; }
        public int ReadRequests { get; private set; }
        public int DeleteRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/v1/namespaces")
            {
                CreateRequests++;
                var json = await request.Content!.ReadAsStringAsync(cancellationToken);
                CreatedNamespace = JsonSerializer.Deserialize<V1Namespace>(json);
                return (int)CreateStatusCode < 400
                    ? Response(
                        request,
                        CreateStatusCode,
                        SerializeNamespace(CreatedNamespace ??
                                           throw new InvalidOperationException("The namespace request body is required.")))
                    : Response(request, CreateStatusCode, SerializeStatus(CreateStatusCode));
            }

            if (request.Method == HttpMethod.Get &&
                path.StartsWith("/api/v1/namespaces/", StringComparison.Ordinal))
            {
                var requestIndex = ReadRequests;
                ReadRequests++;
                var readStatusCode = ReadStatusCodes is { Count: > 0 }
                    ? ReadStatusCodes[Math.Min(requestIndex, ReadStatusCodes.Count - 1)]
                    : ReadStatusCode;
                return readStatusCode == HttpStatusCode.OK
                    ? Response(
                        request,
                        HttpStatusCode.OK,
                        SerializeNamespace(ExistingNamespace ??
                                           throw new InvalidOperationException("An existing namespace is required.")))
                    : Response(request, readStatusCode, SerializeStatus(readStatusCode));
            }

            if (request.Method == HttpMethod.Delete &&
                path.StartsWith("/api/v1/namespaces/", StringComparison.Ordinal))
            {
                DeleteRequests++;
                return Response(request, DeleteStatusCode, SerializeStatus(DeleteStatusCode));
            }

            return Response(request, HttpStatusCode.NotFound, SerializeStatus(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Response(
            HttpRequestMessage request,
            HttpStatusCode statusCode,
            string body) =>
            new(statusCode)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

        private static string SerializeNamespace(V1Namespace value) =>
            JsonSerializer.Serialize(new
            {
                apiVersion = value.ApiVersion,
                kind = value.Kind,
                metadata = new
                {
                    name = value.Metadata.Name,
                    labels = value.Metadata.Labels
                }
            });

        private static string SerializeStatus(HttpStatusCode statusCode) =>
            JsonSerializer.Serialize(new
            {
                apiVersion = "v1",
                kind = "Status",
                status = (int)statusCode < 400 ? "Success" : "Failure",
                reason = statusCode.ToString(),
                code = (int)statusCode,
                message = "provider-secret-token"
            });
    }
}
