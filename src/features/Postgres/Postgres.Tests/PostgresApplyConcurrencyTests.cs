using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using k8s;
using k8s.Autorest;
using Microsoft.FSharp.Core;
using Pole.Features.Core.Kubernetes;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application.Kubernetes;
using Pole.Features.Postgres.Domain;
using Pole.Features.Secrets.Contracts;
using Xunit;

namespace Pole.Features.Postgres.Tests;

public sealed class PostgresApplyConcurrencyTests
{
    [Fact]
    public async Task Status_conflict_reloads_version_and_preserves_desired_spec_and_controller_metadata()
    {
        using var api = new ClusterApi();
        api.OnConflict = current =>
        {
            current["metadata"]!["labels"]!["cnpg.io/cluster"] = "operator-label";
            current["metadata"]!["annotations"]!["cnpg.io/reloadedAt"] = "operator-annotation";
            current["metadata"]!["finalizers"] = new JsonArray("operator-finalizer");
        };
        using var client = Client(api);
        await ApplyAsync(client);
        Assert.Equal(2, api.Reads);
        Assert.Equal(2, api.Updates.Count);
        Assert.Equal("2", api.Updates[1]["metadata"]!["resourceVersion"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(api.Updates[0]["spec"], api.Updates[1]["spec"]));
        Assert.Equal(
            TestData.TenantId,
            api.Updates[1]["metadata"]!["labels"]!["pole.io/tenant-id"]!.GetValue<string>());
        Assert.Equal("operator-label", api.Updates[1]["metadata"]!["labels"]!["cnpg.io/cluster"]!.GetValue<string>());
        Assert.Equal(
            "operator-annotation",
            api.Updates[1]["metadata"]!["annotations"]!["cnpg.io/reloadedAt"]!.GetValue<string>());
        Assert.Equal("operator-finalizer", api.Updates[1]["metadata"]!["finalizers"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Normal_resource_reconciliation_preserves_independent_backup_policy_plugins()
    {
        var plugins = JsonNode.Parse(
            """[{"name":"barman-cloud.cloudnative-pg.io","isWALArchiver":true,"parameters":{"barmanObjectName":"archive-test"}}]""");
        using var api = new ClusterApi { RemainingConflicts = 0, ExistingPlugins = plugins };
        using var client = Client(api);
        await ApplyAsync(client);
        Assert.True(JsonNode.DeepEquals(plugins, Assert.Single(api.Updates)["spec"]!["plugins"]));
    }

    [Theory]
    [InlineData("uid")]
    [InlineData("spec")]
    [InlineData("tenant")]
    [InlineData("deployment")]
    [InlineData("generation")]
    [InlineData("native-generation")]
    [InlineData("deletion")]
    [InlineData("owner")]
    [InlineData("revision")]
    public async Task Concurrent_intent_or_identity_changes_preserve_conflict_without_overwriting(string mutation)
    {
        using var api = new ClusterApi();
        api.OnConflict = current =>
        {
            switch (mutation)
            {
                case "uid":
                    current["metadata"]!["uid"] = "replacement-native-uid";
                    break;
                case "spec":
                    current["spec"]!["instances"] = 9;
                    break;
                case "tenant":
                    current["metadata"]!["labels"]!["pole.io/tenant-id"] = "other-tenant";
                    break;
                case "deployment":
                    current["metadata"]!["labels"]!["pole.io/resource-deployment-id"] = "other-deployment";
                    break;
                case "generation":
                    current["metadata"]!["labels"]!["pole.io/deployment-generation"] = "99";
                    break;
                case "native-generation":
                    current["metadata"]!["generation"] = 2;
                    break;
                case "deletion":
                    current["metadata"]!["deletionTimestamp"] = "2026-10-21T00:00:00Z";
                    break;
                case "owner":
                    current["metadata"]!["ownerReferences"] = new JsonArray(new JsonObject { ["uid"] = "other-owner" });
                    break;
                case "revision":
                    current["metadata"]!["annotations"]!["pole.server/revision"] = "99";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        };
        using var client = Client(api);
        var error = await Assert.ThrowsAsync<HttpOperationException>(() => ApplyAsync(client));
        Assert.Equal(HttpStatusCode.Conflict, error.Response.StatusCode);
        Assert.Single(api.Updates);
    }

    [Fact]
    public async Task Persistent_conflicts_are_bounded_and_keep_the_original_failure()
    {
        using var api = new ClusterApi { RemainingConflicts = int.MaxValue };
        using var client = Client(api);
        var error = await Assert.ThrowsAsync<HttpOperationException>(() => ApplyAsync(client));
        Assert.Equal(HttpStatusCode.Conflict, error.Response.StatusCode);
        Assert.Equal(3, api.Reads);
        Assert.Equal(3, api.Updates.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Nonconflict_rejections_are_not_retried(HttpStatusCode status)
    {
        using var api = new ClusterApi { UpdateStatus = status };
        using var client = Client(api);
        var error = await Assert.ThrowsAsync<HttpOperationException>(() => ApplyAsync(client));
        Assert.Equal(status, error.Response.StatusCode);
        Assert.Single(api.Updates);
    }

    [Fact]
    public async Task Cancellation_after_conflict_prevents_further_requests()
    {
        using var cancellation = new CancellationTokenSource();
        using var api = new ClusterApi { OnConflict = _ => cancellation.Cancel() };
        using var client = Client(api);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ApplyAsync(client, cancellation.Token));
        Assert.Equal(1, api.Reads);
        Assert.Single(api.Updates);
    }

    [Fact]
    public async Task Successful_create_does_not_read_or_replace()
    {
        using var api = new ClusterApi { CreateStatus = HttpStatusCode.Created };
        using var client = Client(api);
        await ApplyAsync(client);
        Assert.Equal(1, api.Creates);
        Assert.Equal(0, api.Reads);
        Assert.Empty(api.Updates);
    }

    [Fact]
    public async Task Transport_failure_is_not_retried()
    {
        using var api = new ClusterApi { ThrowTransport = true };
        using var client = Client(api);
        await Assert.ThrowsAsync<HttpRequestException>(() => ApplyAsync(client));
        Assert.Single(api.Updates);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Missing_native_identity_cannot_be_replaced(string? uid)
    {
        using var api = new ClusterApi { NativeUid = uid };
        using var client = Client(api);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(client));
        Assert.Empty(api.Updates);
    }

    private static Kubernetes Client(DelegatingHandler terminal) => new(
        new KubernetesClientConfiguration { Host = "http://postgres.test" },
        terminal);

    private static Task ApplyAsync(IKubernetes client, CancellationToken cancellationToken = default)
    {
        var cluster = PostgresCluster.Create(
            TestData.TenantId,
            "test",
            "507f1f77bcf86cd799439012",
            17,
            TestData.DefaultStorageProfileId,
            new CreatePostgresClusterOptions(
                FSharpOption<string>.Some("507f1f77bcf86cd799439013"),
                1,
                20,
                PostgresReplicationMode.Asynchronous));
        IPostgresClusterDeploymentManager manager = new PostgresClusterDeploymentManager(
            new PostgresSettings
            {
                OperatorImage = new PostgresImageSettings { Server = "ghcr.io", Name = "cloudnative-pg/postgresql" },
            });
        return manager.ApplyAsync(
            client,
            cluster,
            new SecretProjectionReference("secret", "projection", 2),
            "pole-data",
            "standard",
            new KubernetesComputeConfiguration(
                new KubernetesComputeResourceValues(250, 256),
                new KubernetesComputeResourceValues(1000, 1024),
                ComputeArchitecture.Any,
                [],
                [],
                [],
                [],
                [],
                null),
            new PostgresResourceDeploymentMetadata(
                "deployment",
                "scope",
                "cell",
                TestData.DefaultDeploymentTargetId,
                1),
            cancellationToken);
    }

    private sealed class ClusterApi : DelegatingHandler
    {
        private int _conflicts;
        private JsonObject _current = null!;
        public JsonNode? ExistingPlugins { get; init; }
        public int Creates { get; private set; }
        public int Reads { get; private set; }
        public List<JsonObject> Updates { get; } = [];
        public int RemainingConflicts { get; init; } = 1;
        public HttpStatusCode? UpdateStatus { get; init; }
        public bool ThrowTransport { get; init; }
        public string? NativeUid { get; init; } = "original-native-uid";
        public HttpStatusCode CreateStatus { get; init; } = HttpStatusCode.Conflict;
        public Action<JsonObject>? OnConflict { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage Response(HttpStatusCode status, JsonNode? body = null)
            {
                var response = Reply(status, body);
                response.RequestMessage = request;
                return response;
            }

            Assert.StartsWith(
                "/apis/postgresql.cnpg.io/v1/namespaces/pole-data/clusters",
                request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Post)
            {
                Creates++;
                _current = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                if (ExistingPlugins is not null)
                    _current["spec"]!["plugins"] = ExistingPlugins.DeepClone();
                _current["metadata"]!["uid"] = NativeUid;
                _current["metadata"]!["generation"] = 1;
                _current["metadata"]!["resourceVersion"] = "1";
                return Response(CreateStatus, _current);
            }

            if (request.Method == HttpMethod.Get)
            {
                Reads++;
                return Response(HttpStatusCode.OK, _current);
            }

            if (request.Method == HttpMethod.Put)
            {
                Updates.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject());
                if (ThrowTransport)
                    throw new HttpRequestException("Controlled transport failure.");
                if (UpdateStatus is { } status)
                    return Response(status);
                if (_conflicts++ < RemainingConflicts)
                {
                    _current["metadata"]!["resourceVersion"] = (_conflicts + 1).ToString();
                    _current["status"] = new JsonObject { ["phase"] = "operator-progress" };
                    OnConflict?.Invoke(_current);
                    return Response(HttpStatusCode.Conflict);
                }

                return Response(HttpStatusCode.OK, Updates[^1]);
            }

            throw new InvalidOperationException("Unexpected Kubernetes request method.");
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, JsonNode? body = null) => new(status)
        {
            Content = new StringContent(
                (int)status < 400
                    ? body!.ToJsonString()
                    : JsonSerializer.Serialize(
                        new
                        {
                            apiVersion = "v1", kind = "Status", status = "Failure", reason = status.ToString(),
                            code = (int)status,
                        }),
                Encoding.UTF8,
                "application/json"),
        };
    }
}
