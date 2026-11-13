using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using k8s;
using k8s.Autorest;
using Microsoft.FSharp.Core;
using Pole.Features.Backups.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Postgres.Application.Backups;
using Pole.Features.Postgres.Application.Clusters;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Xunit;

namespace Pole.Features.Postgres.Tests;

public sealed class PostgresBackupReconciliationTests
{
    private static readonly BackupArchiveContext Archive = new(
        new("archive-a", "server", new("https://s3.invalid", "bucket", "p")),
        new("tenant", "pole/postgresql", "service"),
        new("17", 10, 1, 1),
        new("group", "scope", "target", "cell", "ns", "deployment", "storage", 1, "compute", 1));

    private static readonly BackupCredentialProjection Projection = new("projection", "access", "secret");

    private static readonly BackupSchedule Daily = new("5 2 * * *", false, 7);

    [Fact]
    public async Task Late_worker_cannot_reenable_a_retired_schedule_or_overwrite_a_newer_revision()
    {
        using var api = new NativeApi();
        using var client = new Kubernetes(new KubernetesClientConfiguration { Host = "http://test.invalid" }, api);
        var provider = Provider(client);
        var second = Archive with
        {
            Location = Archive.Location with { ArchiveId = "archive-b", ServerName = "second" }
        };
        await Applied(new("policy", Archive, new ConfigureBackupPolicy(Daily, Projection), 1));
        await Applied(
            new("policy", second, new ConfigureBackupPolicy(Daily with { RetentionDays = 14 }, Projection), 2));
        var old = api.Get("scheduledbackups", "schedule-archive-a");
        Assert.True(old["spec"]!["suspend"]!.GetValue<bool>());
        var writes = api.Writes;
        await Applied(
            new("policy", second, new ConfigureBackupPolicy(Daily with { RetentionDays = 14 }, Projection), 2));
        Assert.Equal(writes, api.Writes);
        Assert.IsType<BackupPolicySuperseded>(
            (await provider.ApplyScheduleAsync(
                new("policy", Archive, new ConfigureBackupPolicy(Daily, Projection), 1),
                default)).Value);
        Assert.Equal(writes, api.Writes);
        await Applied(new("policy", second, new StopBackupPolicy(), 3));
        Assert.Empty(api.Get("clusters", "pg-service")["spec"]!["plugins"]!.AsArray());
        var third = Archive with { Location = Archive.Location with { ArchiveId = "archive-c" } };
        await Applied(new("replacement", third, new ConfigureBackupPolicy(Daily, Projection), 1));
        writes = api.Writes;
        Assert.IsType<BackupPolicyAlreadyActive>(
            (await provider.ApplyScheduleAsync(
                new("policy", second, new ConfigureBackupPolicy(Daily, Projection), 2),
                default)).Value);
        Assert.Equal(writes, api.Writes);

        async Task Applied(ApplyBackupScheduleRequest request) =>
            Assert.IsType<BackupPolicyApplied>((await provider.ApplyScheduleAsync(request, default)).Value);
    }

    [Fact]
    public async Task Manual_run_observes_the_original_native_backup_after_provider_restart()
    {
        using var api = new NativeApi();
        using var client = new Kubernetes(new KubernetesClientConfiguration { Host = "http://test.invalid" }, api);
        await Provider(client)
            .ApplyScheduleAsync(
                new("policy", Archive, new ConfigureBackupPolicy(Daily with { Suspended = true }, Projection), 1),
                default);
        var operation = new BackupCopyRequest("operation", Archive, Projection);
        var pending = Assert.IsType<BackupProviderResult<BackupProviderProgress>.Success>(
            await Provider(client).RunAsync(operation, default));
        Assert.Equal(BackupProviderPhase.Running, pending.Value.Phase);
        var backup = api.Get("backups", "backup-operation");
        backup["status"] = JsonNode.Parse("""{"phase":"completed","backupId":"20261112T140000"}""");
        var writes = api.Writes;
        var done = Assert.IsType<BackupProviderResult<BackupProviderProgress>.Success>(
            await Provider(client).RunAsync(operation, default));
        Assert.Equal(BackupProviderPhase.Succeeded, done.Value.Phase);
        Assert.Equal("20261112T140000", done.Value.ProviderBackupId);
        Assert.Equal(writes, api.Writes);
    }

    [Fact]
    public async Task Schedules_and_manual_copies_wait_for_the_Barman_sidecar_on_all_ready_instances()
    {
        using var api = new NativeApi { StandbyHasSidecar = false };
        using var client = new Kubernetes(new KubernetesClientConfiguration { Host = "http://test.invalid" }, api);
        var provider = Provider(client);
        var desired = new ApplyBackupScheduleRequest(
            "policy",
            Archive,
            new ConfigureBackupPolicy(Daily, Projection),
            1);
        var operation = new BackupCopyRequest("operation", Archive, Projection);
        Assert.IsType<BackupPolicyPending>((await provider.ApplyScheduleAsync(desired, default)).Value);
        Assert.True(api.Get("scheduledbackups", "schedule-archive-a")["spec"]!["suspend"]!.GetValue<bool>());
        var writes = api.Writes;
        var retry = Assert.IsType<BackupProviderResult<BackupProviderProgress>.Failure>(
            await provider.RunAsync(operation, default));
        Assert.Equal("backup-plugin-not-ready", retry.Code);
        Assert.True(retry.Retryable);
        Assert.Equal(writes, api.Writes);

        api.StandbyHasSidecar = true;
        Assert.IsType<BackupPolicyApplied>((await provider.ApplyScheduleAsync(desired, default)).Value);
        Assert.False(api.Get("scheduledbackups", "schedule-archive-a")["spec"]!["suspend"]!.GetValue<bool>());
        await provider.RunAsync(operation, default);
        var native = api.Get("backups", "backup-operation");
        native["status"] = JsonNode.Parse("""{"phase":"walArchivingFailing"}""");
        Assert.Equal(
            BackupProviderPhase.Running,
            Assert.IsType<BackupProviderResult<BackupProviderProgress>.Success>(
                    await provider.RunAsync(operation, default))
                .Value.Phase);
        native["status"]
            = JsonNode.Parse("""{"phase":"failed","error":"requested plugin is not available: details"}""");
        var failure = Assert.IsType<BackupProviderResult<BackupProviderProgress>.Failure>(
            await provider.RunAsync(operation, default));
        Assert.Equal("backup-plugin-unavailable", failure.Code);
        Assert.False(failure.Retryable);
    }

    [Theory]
    [InlineData("missing-source", typeof(BackupSourceUnavailable))]
    [InlineData("source-ownership", typeof(BackupNativeOwnershipConflict))]
    [InlineData("source-identity", typeof(BackupNativeIdentityUnavailable))]
    [InlineData("policy-revision", typeof(BackupPolicyRevisionUnreadable))]
    public async Task Policy_authority_failures_are_typed_and_do_not_write(string fault, Type expected)
    {
        using var api = new NativeApi();
        using var client = new Kubernetes(new KubernetesClientConfiguration { Host = "http://test.invalid" }, api);
        var source = api.Get("clusters", "pg-service");
        switch (fault)
        {
            case "missing-source":
                api.Remove("clusters", "pg-service");
                break;
            case "source-ownership":
                source["metadata"]!["labels"]!["pole.io/tenant-id"] = "another-tenant";
                break;
            case "source-identity":
                source["metadata"]!["resourceVersion"] = null;
                break;
            case "policy-revision":
                source["metadata"]!["annotations"] = JsonNode.Parse(
                    """{"backups.pole.io/policy-id":"policy","backups.pole.io/policy-revision":"invalid"}""");
                break;
        }

        var result = await Provider(client)
            .ApplyScheduleAsync(new("policy", Archive, new ConfigureBackupPolicy(Daily, Projection), 1), default);
        Assert.IsType(expected, result.Value);
        Assert.Equal(0, api.Writes);
    }

    [Fact]
    public async Task Policy_cannot_mutate_a_foreign_owned_ObjectStore()
    {
        using var api = new NativeApi();
        using var client = new Kubernetes(new KubernetesClientConfiguration { Host = "http://test.invalid" }, api);
        var provider = Provider(client);
        var desired = new ApplyBackupScheduleRequest(
            "policy",
            Archive,
            new ConfigureBackupPolicy(Daily, Projection),
            1);
        Assert.IsType<BackupPolicyApplied>((await provider.ApplyScheduleAsync(desired, default)).Value);
        api.Get("objectstores", "archive-archive-a")["metadata"]!["labels"]!["pole.io/tenant-id"] = "another-tenant";
        var writes = api.Writes;
        Assert.IsType<BackupNativeOwnershipConflict>(
            (await provider.ApplyScheduleAsync(desired with { Revision = 2 }, default)).Value);
        Assert.Equal(writes, api.Writes);
    }

    [Fact]
    public async Task Policy_retries_a_native_CAS_conflict_without_creating_another_archive()
    {
        using var api = new NativeApi();
        using var client = new Kubernetes(new KubernetesClientConfiguration { Host = "http://test.invalid" }, api);
        var provider = Provider(client);
        var desired = new ApplyBackupScheduleRequest(
            "policy",
            Archive,
            new ConfigureBackupPolicy(Daily, Projection),
            1);
        Assert.IsType<BackupPolicyApplied>((await provider.ApplyScheduleAsync(desired, default)).Value);
        var store = api.Get("objectstores", "archive-archive-a");
        var uid = store["metadata"]!["uid"]!.GetValue<string>();
        var updated = desired with
        {
            Revision = 2,
            Configuration = new ConfigureBackupPolicy(Daily with { RetentionDays = 14 }, Projection)
        };
        api.NextWriteFailure = HttpStatusCode.Conflict;
        Assert.IsType<BackupNativeWriteConflict>((await provider.ApplyScheduleAsync(updated, default)).Value);
        Assert.Equal("7d", store["spec"]!["retentionPolicy"]!.GetValue<string>());
        Assert.IsType<BackupPolicyApplied>((await provider.ApplyScheduleAsync(updated, default)).Value);
        Assert.Equal(uid, api.Get("objectstores", "archive-archive-a")["metadata"]!["uid"]!.GetValue<string>());
        Assert.Equal("14d", store["spec"]!["retentionPolicy"]!.GetValue<string>());
    }

    [Fact]
    public async Task Policy_waits_for_WAL_archiving_and_preserves_other_plugins()
    {
        using var api = new NativeApi();
        using var client = new Kubernetes(new KubernetesClientConfiguration { Host = "http://test.invalid" }, api);
        var source = api.Get("clusters", "pg-service");
        source["spec"]!["plugins"] = JsonNode.Parse("""[{"name":"another-provider","parameters":{"key":"value"}}]""");
        var wal = source["status"]!["conditions"]!.AsArray()
            .Single(node => node!["type"]!.GetValue<string>() == "ContinuousArchiving")!;
        wal["status"] = "False";
        var desired = new ApplyBackupScheduleRequest(
            "policy",
            Archive,
            new ConfigureBackupPolicy(Daily, Projection),
            1);
        var provider = Provider(client);
        Assert.IsType<BackupPolicyPending>((await provider.ApplyScheduleAsync(desired, default)).Value);
        Assert.True(api.Get("scheduledbackups", "schedule-archive-a")["spec"]!["suspend"]!.GetValue<bool>());
        wal["status"] = "True";
        Assert.IsType<BackupPolicyApplied>((await provider.ApplyScheduleAsync(desired, default)).Value);
        Assert.False(api.Get("scheduledbackups", "schedule-archive-a")["spec"]!["suspend"]!.GetValue<bool>());
        Assert.Equal(
            "value",
            source["spec"]!["plugins"]!.AsArray()
                .Single(node => node!["name"]!.GetValue<string>() == "another-provider")!["parameters"]!["key"]!
                .GetValue<string>());
    }

    [Fact]
    public async Task Policy_keeps_unexpected_infrastructure_failure_and_caller_cancellation_on_the_exception_path()
    {
        using var api = new NativeApi { ReadFailure = HttpStatusCode.InternalServerError };
        using var client = new Kubernetes(new KubernetesClientConfiguration { Host = "http://test.invalid" }, api);
        var desired = new ApplyBackupScheduleRequest(
            "policy",
            Archive,
            new ConfigureBackupPolicy(Daily, Projection),
            1);
        await Assert.ThrowsAsync<HttpOperationException>(() => Provider(client).ApplyScheduleAsync(desired, default));
        api.ReadFailure = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Provider(client).ApplyScheduleAsync(desired, new CancellationToken(true)));
        Assert.Equal(0, api.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_observation_accepts_the_exact_selected_copy_or_time(bool byTime)
    {
        using var api = new NativeApi();
        using var client = new Kubernetes(new KubernetesClientConfiguration { Host = "http://test.invalid" }, api);
        var (provider, request) = RecoverySubject(api, client, byTime);
        var result = Assert.IsType<BackupProviderResult<BackupProviderProgress>.Success>(
            await provider.RestoreAsync(request, default));
        Assert.Equal(BackupProviderPhase.Succeeded, result.Value.Phase);
        Assert.Equal(0, api.Writes);
    }

    [Theory]
    [InlineData("origin", "restore-identity-conflict")]
    [InlineData("placement", "restore-placement-changed")]
    [InlineData("ownership", "restore-identity-conflict")]
    [InlineData("target", "restore-target-mismatch")]
    public async Task Recovery_observation_rejects_mismatched_authority_before_reporting_success(
        string fault,
        string expected)
    {
        using var api = new NativeApi();
        using var client = new Kubernetes(new KubernetesClientConfiguration { Host = "http://test.invalid" }, api);
        var (provider, request) = RecoverySubject(api, client, false, fault);
        var result = Assert.IsType<BackupProviderResult<BackupProviderProgress>.Failure>(
            await provider.RestoreAsync(request, default));
        Assert.Equal(expected, result.Code);
        Assert.False(result.Retryable);
        Assert.Equal(0, api.Writes);
    }

    private static (IBackupProvider Provider, RestoreBackupRequest Request) RecoverySubject(
        NativeApi api,
        IKubernetes client,
        bool byTime,
        string fault = "")
    {
        var time = DateTimeOffset.Parse("2026-11-12T14:00:00.123456Z");
        const string backupId = "20261112T140000";
        BackupRecoveryTarget selected
            = byTime ? new BackupTimeRecoveryTarget(time) : new BackupCopyRecoveryTarget(backupId);
        PostgresRecoveryTarget persistedTarget = byTime
            ? new PostgresTimeRecoveryTarget(time.UtcDateTime)
            : new PostgresCopyRecoveryTarget(backupId);
        var origin = new PostgresRecoveryOrigin(
            fault == "origin" ? "another-operation" : "restore-operation",
            Archive.Location.ArchiveId,
            Archive.Location.ServerName,
            persistedTarget);
        var created = PostgresCluster.Create(
            "tenant",
            "restored",
            "group",
            17,
            "storage",
            new CreatePostgresClusterOptions(FSharpOption<string>.Some("service"), 1, 10));
        var cluster = new PostgresCluster(
            created.Id,
            created.TenantId,
            created.Name,
            created.GroupId,
            new PostgresClusterSpec(1, 10, "storage", 17, new RecoveryPostgresBootstrap(origin)),
            created.Revision,
            created.Lifecycle,
            created.Provisioning,
            created.PlanningFence);
        var deployment = new PlacementSummaryDto(
            "restored-deployment",
            "scope",
            fault == "placement" ? "another-cell" : "cell",
            "target",
            1,
            1,
            ResourceDeploymentStatus.Applied,
            null,
            new(new("compute", 1), "Database", new(250, 256), new(500, 512), []),
            [],
            1,
            1,
            null,
            1,
            ResourceDeploymentDesiredOperation.Apply,
            new("None", "Ready", "Test placement", DateTime.UtcNow));
        LoadPostgresClusterById load = (
            tenant, resource,
            _) =>
        {
            Assert.Equal(cluster.TenantId, tenant);
            Assert.Equal(cluster.Id, resource);
            return Task.FromResult(FSharpOption<PostgresCluster>.Some(cluster));
        };
        LoadResourceDeployment loadPlacement = (_, _) => Task.FromResult<PlacementSummaryDto?>(deployment);
        IBackupProvider provider = new PostgresBackupProvider(
            new Clients(client),
            load,
            loadPlacement,
            null!,
            new PostgresSettings(),
            null!);
        var native = api.Get("clusters", "pg-service");
        native["metadata"]!["labels"]!["pole.io/resource-deployment-id"]
            = fault == "ownership" ? "foreign-deployment" : deployment.DeploymentId;
        var target = byTime
            ? new JsonObject { ["targetTime"] = time.ToString("O") }
            : new JsonObject
                { ["backupID"] = fault == "target" ? "another-backup" : backupId, ["targetImmediate"] = true };
        native["spec"]!["bootstrap"] = new JsonObject
        {
            ["recovery"] = new JsonObject { ["source"] = "origin", ["recoveryTarget"] = target },
        };
        var archive = Archive with { Source = Archive.Source with { ResourceId = "original-source" } };
        return (provider,
            new RestoreBackupRequest(
                "restore-operation",
                archive,
                Projection,
                new RestoreBackupResource(cluster.Id, cluster.Name, null),
                selected));
    }

    private static IBackupProvider Provider(IKubernetes client) => new PostgresBackupProvider(
        new Clients(client),
        null!,
        null!,
        null!,
        new PostgresSettings(),
        null!);

    private sealed class Clients(
        IKubernetes client
    ) : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(string id, CancellationToken ct = default)
        {
            Assert.Equal(Archive.Placement.DeploymentTargetId, id);
            return Task.FromResult(client);
        }

        public Task<IKubernetes> GetForCleanupAsync(string id, CancellationToken ct = default) => GetAsync(id, ct);
        public Task<IKubernetes> GetDefaultAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NativeApi : DelegatingHandler
    {
        private readonly Dictionary<(string Plural, string Name), JsonNode> objects = [];
        private int version = 1;
        public int Writes { get; private set; }
        public bool StandbyHasSidecar { get; set; } = true;
        public HttpStatusCode? NextWriteFailure { get; set; }
        public HttpStatusCode? ReadFailure { get; set; }

        public NativeApi()
        {
            var source = JsonNode.Parse(
                """
                {"metadata":{"name":"pg-service","uid":"source-uid","resourceVersion":"1","labels":{}},
                 "spec":{"instances":2},"status":{"currentPrimary":"pg-service-1","conditions":[{"type":"Ready","status":"True"},{"type":"ContinuousArchiving","status":"True"}],
                 "pluginStatus":[{"name":"barman-cloud.cloudnative-pg.io"}]}}
                """)!;
            foreach (var pair in PostgresBackupManifests.sourceLabels(Archive))
                source["metadata"]!["labels"]![pair.Item1] = pair.Item2;
            objects[("clusters", "pg-service")] = source;
        }

        public JsonNode Get(string plural, string name) => objects[(plural, name)];
        public void Remove(string plural, string name) => objects.Remove((plural, name));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (request.Method == HttpMethod.Get && ReadFailure is { } readFailure)
                return Reply(request, readFailure, JsonNode.Parse("""{"kind":"Status"}""")!);
            if (request.Method != HttpMethod.Get && NextWriteFailure is { } writeFailure)
            {
                NextWriteFailure = null;
                return Reply(request, writeFailure, JsonNode.Parse("""{"kind":"Status"}""")!);
            }

            var path = request.RequestUri!.AbsolutePath.Split('/');
            if (request.RequestUri.AbsolutePath == "/api/v1/namespaces/ns/pods")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                var list = JsonNode.Parse("""{"apiVersion":"v1","kind":"PodList","metadata":{},"items":[]}""")!;
                list["items"]!.AsArray().Add(Instance("pg-service-1", true));
                list["items"]!.AsArray().Add(Instance("pg-service-2", StandbyHasSidecar));
                return Reply(request, HttpStatusCode.OK, list);
            }

            var collection = path[6];
            if (request.Method == HttpMethod.Get)
                return objects.TryGetValue((collection, path[7]), out var found)
                    ? Reply(request, HttpStatusCode.OK, found)
                    : Reply(request, HttpStatusCode.NotFound, JsonNode.Parse("""{"kind":"Status","code":404}""")!);

            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            if (request.Method == HttpMethod.Post)
            {
                var metadata = body["metadata"]!;
                var name = metadata["name"]!.GetValue<string>();
                Assert.True(objects.TryAdd((collection, name), body));
                metadata["uid"] = "uid-" + name;
            }
            else
            {
                Assert.Equal(HttpMethod.Patch, request.Method);
                var current = Get(collection, path[7]);
                Assert.Equal(
                    current["metadata"]!["resourceVersion"]!.GetValue<string>(),
                    body["metadata"]!["resourceVersion"]!.GetValue<string>());
                Assert.Equal(
                    current["metadata"]!["uid"]!.GetValue<string>(),
                    body["metadata"]!["uid"]!.GetValue<string>());
                foreach (var key in new[] { "metadata", "spec" })
                foreach (var item in body[key]!.AsObject())
                    current[key]![item.Key] = item.Value?.DeepClone();
                body = current;
            }

            body["metadata"]!["resourceVersion"] = (++version).ToString();
            Writes++;
            return Reply(request, HttpStatusCode.OK, body);
        }

        private static JsonNode Instance(string name, bool sidecarReady)
        {
            var pod = JsonNode.Parse(
                """
                {"metadata":{"ownerReferences":[{"apiVersion":"postgresql.cnpg.io/v1","kind":"Cluster","name":"pg-service","uid":"source-uid"}]},
                 "status":{"conditions":[{"type":"Ready","status":"True"}],"initContainerStatuses":[]}}
                """)!;
            pod["metadata"]!["name"] = name;
            if (sidecarReady)
                pod["status"]!["initContainerStatuses"]!.AsArray()
                    .Add(
                        JsonNode.Parse(
                            """
                            {"name":"plugin-barman-cloud","image":"test","imageID":"test","ready":true,"restartCount":0,"state":{"running":{}}}
                            """));
            return pod;
        }

        private static HttpResponseMessage Reply(
            HttpRequestMessage request,
            HttpStatusCode code,
            JsonNode response) => new(code)
        {
            RequestMessage = request,
            Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json")
        };
    }
}
