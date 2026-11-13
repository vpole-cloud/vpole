using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Operations;
using Pole.Features.Backups.Application.Reconciliation;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Backups.Events;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Domain;
using Pole.Observability;
using Pole.Testing;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.Backups.Tests.Integration;

[Collection("backup-mongo")]
public sealed class BackupHistoryLifecycleTests(
    MongoReplicaSetFixture mongo
) : IAsyncLifetime
{
    private const string Tenant = TestPlatformDefaults.TenantId;
    private readonly BackupTestSetup setup = new(mongo);
    private IMongoCollection<BackupArchive> Archives => mongo.Database.GetCollection<BackupArchive>("BackupArchives");

    private IMongoCollection<BackupOperation> Operations =>
        mongo.Database.GetCollection<BackupOperation>("BackupOperations");

    public Task InitializeAsync() => mongo.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_new_scope_resumes_the_persisted_page_and_terminal_history_cannot_regress()
    {
        await using var host = await setup.StartAsync();
        var archive = await CreateArchiveAsync(host);
        var provider = host.Services.GetRequiredService<TestBackupProvider>();
        var created = DateTimeOffset.UtcNow.AddHours(-1);
        var count = 0;
        provider.ReadHistory = (request, _) => Task.FromResult<ReadBackupHistoryOutcome>(
            ++count switch
            {
                1 => new BackupHistoryBatch(
                    new PageNativeBackupHistory(new("publisher", "opaque"), "page-2"),
                    1,
                    [new("native", true)],
                    [new("native", new NativeScheduledBackupRunning(), created, created)],
                    200),
                2 => new BackupHistoryBatch(
                    new FollowNativeBackupHistory(new("publisher", "opaque")),
                    2,
                    [new("native", false)],
                    [
                        new(
                            "native",
                            new NativeScheduledBackupCompleted("backup"),
                            created.AddMinutes(1),
                            created.AddMinutes(2))
                    ],
                    1),
                _ => new BackupHistoryBatch(
                    new FollowNativeBackupHistory(new("publisher", "changed")),
                    2,
                    [new("native", true)],
                    [new("native", new NativeScheduledBackupRunning(), created, created)],
                    1)
            });
        await ObserveAsync(host, archive.Id);
        var paged = Assert.IsType<MonitoringBackupHistory>((await ArchiveAsync(archive.Id)).History);
        Assert.Equal("page-2", Assert.IsType<PagedBackupHistoryPosition>(paged.Checkpoint.Position).Continuation);
        await ObserveAsync(host, archive.Id);
        Assert.IsType<PageNativeBackupHistory>(provider.LastHistoryRequest!.Position);
        var operation = await Operations.Find(o => o.ArchiveId == archive.Id).SingleAsync();
        Assert.Equal(BackupOperationState.Succeeded, operation.State);
        Assert.Equal(
            created.UtcDateTime.Ticks / TimeSpan.TicksPerMillisecond,
            operation.CreatedAt.Ticks / TimeSpan.TicksPerMillisecond);
        Assert.Equal(
            new TraceContextSnapshot(
                archive.History.ExecutionOrigin.TraceId,
                archive.History.ExecutionOrigin.CorrelationId),
            provider.LastHistoryTrace);
        await DueAsync(archive.Id);
        await ObserveAsync(host, archive.Id);
        Assert.Equal(operation, await Operations.Find(o => o.Id == operation.Id).SingleAsync());
        Assert.IsType<FollowNativeBackupHistory>(provider.LastHistoryRequest!.Position);
    }

    [Fact]
    public async Task Batch_history_checkpoint_and_continuation_roll_back_together_and_an_expired_claim_can_retry()
    {
        var fail = false;
        await using var host = await setup.StartAsync(failHistoryContinuation: () => fail);
        var archive = await CreateArchiveAsync(host);
        var provider = host.Services.GetRequiredService<TestBackupProvider>();
        fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ObserveAsync(host, archive.Id));
        Assert.Empty(await Operations.Find(o => o.ArchiveId == archive.Id).ToListAsync());
        var claimed = Assert.IsType<MonitoringBackupHistory>((await ArchiveAsync(archive.Id)).History);
        Assert.IsType<InitialBackupHistoryPosition>(claimed.Checkpoint.Position);
        var lease = Assert.IsType<ClaimedBackupDispatch>(claimed.Dispatch);
        var expired = claimed with
        {
            Revision = claimed.Revision + 1,
            Dispatch = lease with { Lease = lease.Lease with { ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1) } }
        };
        await Archives.UpdateOneAsync(
            a => a.Id == archive.Id,
            Builders<BackupArchive>.Update.Set(a => a.History, expired));
        fail = false;
        await using (var scope = host.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<BackupRecoveryScanner>().RequestDueWorkAsync(default);
        await ObserveAsync(host, archive.Id);
        Assert.IsType<BeginNativeBackupHistory>(provider.LastHistoryRequest!.Position);
        Assert.Equal(2, await Operations.CountDocumentsAsync(o => o.ArchiveId == archive.Id));
        Assert.IsType<FollowingBackupHistoryPosition>(
            Assert.IsType<MonitoringBackupHistory>((await ArchiveAsync(archive.Id)).History).Checkpoint.Position);
    }

    [Fact]
    public async Task
        Backfill_finalization_interrupts_missing_running_rows_in_bounded_steps_without_another_native_read()
    {
        await using var host = await setup.StartAsync();
        var archive = await CreateArchiveAsync(host);
        var now = DateTime.UtcNow.AddMinutes(-5);
        await Operations.InsertManyAsync(
            Enumerable.Range(0, 205)
                .Select(index => BackupOperation.ObserveScheduled(
                    Id(),
                    Tenant,
                    "missing-" + index,
                    archive.PolicyId,
                    archive.Id,
                    "native-" + index,
                    1,
                    "previous-epoch",
                    new RunningScheduledBackup(),
                    now,
                    now,
                    archive.History.ExecutionOrigin)));
        var provider = host.Services.GetRequiredService<TestBackupProvider>();
        var nativeReads = 0;
        provider.ReadHistory = (_, _) =>
        {
            nativeReads++;
            return Task.FromResult<ReadBackupHistoryOutcome>(
                new BackupHistoryBatch(new FollowNativeBackupHistory(new("publisher", "snapshot")), 1, [], [], 0));
        };
        await ObserveAsync(host, archive.Id);
        Assert.Equal(
            200,
            await Operations.CountDocumentsAsync(o =>
                o.ArchiveId == archive.Id && o.Work is ScheduledBackupWork &&
                ((ScheduledBackupWork)o.Work).Execution is InterruptedScheduledBackup));
        Assert.IsType<FinishingBackupHistoryPosition>(
            Assert.IsType<MonitoringBackupHistory>((await ArchiveAsync(archive.Id)).History).Checkpoint.Position);
        await ObserveAsync(host, archive.Id);
        Assert.Equal(1, nativeReads);
        Assert.Equal(
            205,
            await Operations.CountDocumentsAsync(o =>
                o.ArchiveId == archive.Id && o.Work is ScheduledBackupWork &&
                ((ScheduledBackupWork)o.Work).Execution is InterruptedScheduledBackup));
        Assert.IsType<FollowingBackupHistoryPosition>(
            Assert.IsType<MonitoringBackupHistory>((await ArchiveAsync(archive.Id)).History).Checkpoint.Position);
    }

    [Fact]
    public async Task Temporarily_missing_native_source_does_not_end_history_for_a_still_registered_resource()
    {
        await using var host = await setup.StartAsync();
        var archive = await CreateArchiveAsync(host);
        var operation = BackupOperation.ObserveScheduled(
            Id(),
            Tenant,
            "running-before-native-loss",
            archive.PolicyId,
            archive.Id,
            "native",
            1,
            "epoch",
            new RunningScheduledBackup(),
            DateTime.UtcNow,
            DateTime.UtcNow,
            archive.History.ExecutionOrigin);
        await Operations.InsertOneAsync(operation);
        host.Services.GetRequiredService<TestBackupProvider>().ReadHistory = (_, _) =>
            Task.FromResult<ReadBackupHistoryOutcome>(new BackupSourceUnavailable());
        await ObserveAsync(host, archive.Id);
        var monitoring = Assert.IsType<MonitoringBackupHistory>((await ArchiveAsync(archive.Id)).History);
        Assert.Equal("backup-source-unavailable", monitoring.LastFailure);
        Assert.Equal(
            BackupOperationState.Running,
            (await Operations.Find(o => o.Id == operation.Id).SingleAsync()).State);
    }

    [Fact]
    public async Task A_replaced_claim_cannot_commit_native_history_or_its_checkpoint()
    {
        await using var host = await setup.StartAsync();
        var archive = await CreateArchiveAsync(host);
        var provider = host.Services.GetRequiredService<TestBackupProvider>();
        provider.ReadHistory = async (_, _) =>
        {
            var current = await ArchiveAsync(archive.Id);
            var replacement = current.History.RequestDrain(
                new(archive.PolicyId, 2, "publisher"),
                new(Id(), current.History.ExecutionOrigin),
                DateTime.UtcNow);
            await Archives.UpdateOneAsync(
                a => a.Id == archive.Id,
                Builders<BackupArchive>.Update.Set(a => a.History, replacement));
            return new BackupHistoryBatch(
                new FollowNativeBackupHistory(new("publisher", "stale")),
                1,
                [],
                [
                    new(
                        "stale",
                        new NativeScheduledBackupCompleted("backup"),
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow)
                ],
                1);
        };
        await ObserveAsync(host, archive.Id);
        Assert.Empty(await Operations.Find(o => o.ArchiveId == archive.Id).ToListAsync());
        var current = Assert.IsType<MonitoringBackupHistory>((await ArchiveAsync(archive.Id)).History);
        Assert.IsType<DrainingBackupHistory>(current.Mode);
        Assert.IsType<InitialBackupHistoryPosition>(current.Checkpoint.Position);
    }

    [Fact]
    public async Task Destination_switch_waits_for_native_drain_and_unfinished_manual_commands_before_confirming()
    {
        await using var host = await setup.StartAsync();
        var archive = await CreateArchiveAsync(host);
        var provider = host.Services.GetRequiredService<TestBackupProvider>();
        var policies = mongo.Database.GetCollection<BackupPolicy>("BackupPolicies");
        var destinations = mongo.Database.GetCollection<BackupDestination>("BackupDestinations");
        var destination = await destinations.Find(d => d.Id == archive.DestinationId).SingleAsync();
        using var client = BackupTestSetup.Client(host, Tenant);
        using var second = await client.PostAsJsonAsync(
            "/api/v1/backups/destinations",
            new BackupDestinationInput(
                "secondary",
                destination.ResourceGroupId,
                destination.IsolationScopeId,
                destination.Storage.Endpoint,
                destination.Storage.Bucket,
                "replacement",
                destination.Storage.CredentialSecretId,
                destination.Storage.AccessKeyIdKey,
                destination.Storage.SecretAccessKeyKey));
        second.EnsureSuccessStatusCode();
        var replacement = (await second.Content.ReadFromJsonAsync<BackupDestinationDto>())!;
        var original = await policies.Find(p => p.Id == archive.PolicyId).SingleAsync();
        var command = BackupOperation.RequestCopy(
            Id(),
            Tenant,
            "pending-manual",
            archive.PolicyId,
            archive.Id,
            DateTime.UtcNow,
            archive.History.ExecutionOrigin);
        await Operations.InsertOneAsync(command);
        using var changed = await client.PutAsJsonAsync(
            "/api/v1/backups/policies/" + original.Id,
            new
            {
                original.Name, original.ResourceType, original.ResourceId, destinationId = replacement.Id,
                original.Schedule, original.Suspended, original.RetentionDays, expectedRevision = original.Revision
            });
        changed.EnsureSuccessStatusCode();
        var confirmed = 0;
        provider.Apply = request =>
        {
            if (request.Drain is ConfirmedBackupArchiveDrain drain)
            {
                Assert.Equal(archive.Id, drain.ArchiveId);
                Assert.Equal(request.Revision, drain.Suspension.PolicyRevision);
                confirmed++;
                return new BackupPolicyApplied();
            }

            return new BackupPolicyDrainRequired(archive.Id, new(archive.PolicyId, request.Revision, "publisher"));
        };
        var nativeFinished = false;
        provider.ReadHistory = (_, _) => Task.FromResult<ReadBackupHistoryOutcome>(
            new BackupHistoryBatch(
                new FollowNativeBackupHistory(new("publisher", nativeFinished ? "complete" : "running")),
                2,
                [new("copy", !nativeFinished)],
                [
                    new(
                        "copy",
                        nativeFinished
                            ? new NativeScheduledBackupCompleted("copy")
                            : new NativeScheduledBackupRunning(),
                        DateTimeOffset.UtcNow.AddMinutes(-1),
                        DateTimeOffset.UtcNow)
                ],
                1));
        await ApplyNow();
        await ObserveAsync(host, archive.Id);
        await ApplyNow();
        Assert.Equal(0, confirmed);
        nativeFinished = true;
        await DueAsync(archive.Id);
        await ObserveAsync(host, archive.Id);
        Assert.IsType<QuiescentBackupHistory>((await ArchiveAsync(archive.Id)).History);
        await ApplyNow();
        Assert.Equal(0, confirmed);
        await using (var scope = host.Services.CreateAsyncScope())
            Assert.True(
                await scope.ServiceProvider
                    .GetRequiredService<ExecuteBackupOperation>()
                    .ExecuteAsync(Tenant, command.Id, default));
        await ApplyNow();
        Assert.Equal(1, confirmed);
        var applied = await policies.Find(p => p.Id == archive.PolicyId).SingleAsync();
        Assert.Equal(applied.Revision, applied.AppliedRevision);
        provider.ReadHistory = (_, _) =>
            throw new InvalidOperationException("Forget must use its committed drain receipt.");
        using var forgotten = await client.DeleteAsync("/api/v1/backups/archives/" + archive.Id + "?confirmed=true");
        Assert.Equal(System.Net.HttpStatusCode.NoContent, forgotten.StatusCode);

        async Task ApplyNow()
        {
            var policy = await policies.Find(p => p.Id == archive.PolicyId).SingleAsync();
            await policies.UpdateOneAsync(
                p => p.Id == policy.Id,
                Builders<BackupPolicy>.Update.Set(
                    p => p.Dispatch,
                    new AvailableBackupDispatch(DateTime.UtcNow.AddSeconds(-1), policy.Dispatch.Origin)));
            await using var scope = host.Services.CreateAsyncScope();
            Assert.True(
                await scope.ServiceProvider
                    .GetRequiredService<ApplyBackupPolicy>()
                    .ExecuteAsync(Tenant, policy.Id, default));
        }
    }

    [Fact]
    public async Task Recovery_bounds_archive_requests_and_excludes_future_or_unexpired_claims()
    {
        await using var host = await setup.StartAsync();
        var template = await CreateArchiveAsync(host);
        var now = DateTime.UtcNow;
        var copies = Enumerable.Range(0, 125)
            .Select(index => BackupArchive.Create(
                Id(),
                Tenant,
                template.PolicyId,
                template.DestinationId,
                new(
                    template.ResourceType,
                    template.ResourceId,
                    template.ResourceName,
                    template.SourceRevision,
                    template.ServiceVersion,
                    template.StorageGiB,
                    template.Instances),
                template.Storage,
                template.Placement,
                new(Id(), template.History.ExecutionOrigin),
                now.AddMinutes(-10).AddSeconds(index)))
            .ToArray();
        copies[0] = copies[0] with { History = copies[0].History.Claim(new("expired", now.AddSeconds(-1))) };
        copies[1] = copies[1] with { History = copies[1].History.Claim(new("live", now.AddHours(1))) };
        copies[2] = copies[2] with { History = copies[2].History.Retry(now.AddHours(1), TimeSpan.Zero, "not-due") };
        await Archives.InsertManyAsync(copies);
        await using (var scope = host.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<BackupRecoveryScanner>().RequestDueWorkAsync(default);
        var outbox = mongo.Database.GetCollection<OutboxMessage>("OutboxMessages");
        var rows = await outbox.Find(message => message.EventType == "pole.backups/v1/history-work-requested")
            .ToListAsync();
        var ids = copies.Select(copy => copy.Id).ToHashSet();
        var requests = rows
            .Select(row => JsonSerializer.Deserialize<BackupHistoryWorkRequestedEvent>(
                row.PayloadJson,
                EventExtensions.SerializerOptions)!)
            .Where(request => ids.Contains(request.ArchiveId))
            .ToArray();
        Assert.Equal(100, requests.Length);
        Assert.Equal(100, requests.Select(request => request.ArchiveId).Distinct().Count());
        Assert.Contains(requests, request => request.ArchiveId == copies[0].Id);
        Assert.DoesNotContain(
            requests,
            request => request.ArchiveId == copies[1].Id || request.ArchiveId == copies[2].Id);
        Assert.All(
            requests,
            request =>
            {
                Assert.Equal(Tenant, request.EventCore.TenantId);
                Assert.Equal(template.History.ExecutionOrigin.TraceId, request.EventCore.TraceId);
                Assert.Equal(template.History.ExecutionOrigin.CorrelationId, request.EventCore.CorrelationId);
            });
    }

    private async Task<BackupArchive> CreateArchiveAsync(FeatureTestHost host)
    {
        using var client = BackupTestSetup.Client(host, Tenant);
        var input = await BackupTestSetup.StorageAsync(client);
        using var destinationResponse = await client.PostAsJsonAsync("/api/v1/backups/destinations", input);
        destinationResponse.EnsureSuccessStatusCode();
        var destination = (await destinationResponse.Content.ReadFromJsonAsync<BackupDestinationDto>())!;
        var provider = host.Services.GetRequiredService<TestBackupProvider>();
        var source = Id();
        await using var scope = host.Services.CreateAsyncScope();
        await setup.SeedSourceAsync(
            scope.ServiceProvider,
            input,
            ResourceTypeId.Create(provider.ResourceType),
            source,
            Id());
        using var policyResponse = await client.PostAsJsonAsync(
            "/api/v1/backups/policies",
            new BackupPolicyInput("daily", provider.ResourceType, source, destination.Id, "0 2 * * *", false, 30));
        policyResponse.EnsureSuccessStatusCode();
        return await ArchiveAsync((await policyResponse.Content.ReadFromJsonAsync<BackupPolicyDto>())!.ArchiveId);
    }

    private async Task ObserveAsync(FeatureTestHost host, string archiveId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        Assert.True(
            await scope.ServiceProvider
                .GetRequiredService<ObserveBackupArchiveHistory>()
                .ExecuteAsync(Tenant, archiveId, default));
    }

    private async Task DueAsync(string archiveId)
    {
        var current = await ArchiveAsync(archiveId);
        await Archives.ReplaceOneAsync(
            a => a.Id == archiveId,
            current with
            {
                History = current.History.Retry(DateTime.UtcNow.AddSeconds(-1), TimeSpan.Zero, "test-due")
            });
    }

    private Task<BackupArchive> ArchiveAsync(string id) => Archives.Find(a => a.Id == id).SingleAsync();
    private static string Id() => ObjectId.GenerateNewId().ToString();
}
