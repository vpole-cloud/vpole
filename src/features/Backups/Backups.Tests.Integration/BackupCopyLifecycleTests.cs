using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Operations;
using Pole.Features.Backups.Application.Providers;
using Pole.Features.Backups.Application.Reconciliation;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Testing;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.Backups.Tests.Integration;

[Collection("backup-mongo")]
public sealed class BackupCopyLifecycleTests(
    MongoReplicaSetFixture mongo
) : IAsyncLifetime
{
    private const string Route = "/api/v1/backups";
    private readonly BackupTestSetup setup = new(mongo);

    public Task InitializeAsync() => mongo.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_native_copy_can_progress_after_retirement_without_resolving_credentials(bool completed)
    {
        await using var host = await setup.StartAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var provider = host.Services.GetRequiredService<TestBackupProvider>();
        provider.CopyObservation = completed ? new BackupCopyCompleted("copy") : new BackupCopyInProgress();
        var archive = Archive(provider.ResourceType).Retire();
        var operation = Operation(archive);
        var (execute, calls) = CopyWithoutCredentialAccess(scope.ServiceProvider);
        var apply = await execute.ExecuteAsync(operation, archive, default);
        var updated = apply(operation, DateTime.UtcNow);
        Assert.Equal(completed ? BackupOperationState.Succeeded : BackupOperationState.Running, updated.State);
        Assert.Equal(0, provider.RunCount);
        Assert.Equal(0, calls());
    }

    [Fact]
    public async Task A_missing_running_copy_is_not_recreated()
    {
        await using var host = await setup.StartAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var provider = host.Services.GetRequiredService<TestBackupProvider>();
        var archive = Archive(provider.ResourceType);
        var operation = Operation(archive).CopyRunning(DateTime.UtcNow, TimeSpan.Zero);
        var (execute, calls) = CopyWithoutCredentialAccess(scope.ServiceProvider);
        var apply = await execute.ExecuteAsync(operation, archive, default);
        var updated = apply(operation, DateTime.UtcNow);
        Assert.Equal(BackupOperationState.Running, updated.State);
        Assert.Equal("backup-native-copy-missing", updated.FailureCode);
        Assert.Equal(0, provider.RunCount);
        Assert.Equal(0, calls());
    }

    [Fact]
    public async Task A_retired_archive_cannot_start_an_absent_requested_copy()
    {
        await using var host = await setup.StartAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var provider = host.Services.GetRequiredService<TestBackupProvider>();
        var archive = Archive(provider.ResourceType).Retire();
        var operation = Operation(archive);
        var (execute, calls) = CopyWithoutCredentialAccess(scope.ServiceProvider);
        var apply = await execute.ExecuteAsync(operation, archive, default);
        var updated = apply(operation, DateTime.UtcNow);
        Assert.Equal(BackupOperationState.Failed, updated.State);
        Assert.Equal("archive-retired", updated.FailureCode);
        Assert.Equal(0, provider.RunCount);
        Assert.Equal(0, calls());
    }

    [Fact]
    public async Task Retained_archive_credentials_remain_in_use_until_an_existing_manual_native_copy_finishes()
    {
        await using var host = await setup.StartAsync();
        using var client = BackupTestSetup.Client(host, TestPlatformDefaults.TenantId);
        var input = await BackupTestSetup.StorageAsync(client);
        using var first = await client.PostAsJsonAsync(Route + "/destinations", input);
        first.EnsureSuccessStatusCode();
        var destination = (await first.Content.ReadFromJsonAsync<BackupDestinationDto>())!;
        await using var scope = host.Services.CreateAsyncScope();
        var provider = host.Services.GetRequiredService<TestBackupProvider>();
        provider.ObservedRuns = [];
        provider.CopyObservation = new BackupCopyInProgress();
        var source = Id();
        await setup.SeedSourceAsync(
            scope.ServiceProvider,
            input,
            ResourceTypeId.Create(provider.ResourceType),
            source,
            Id());
        using var created = await client.PostAsJsonAsync(
            Route + "/policies",
            new BackupPolicyInput("daily", provider.ResourceType, source, destination.Id, "0 2 * * *", false, 30));
        created.EnsureSuccessStatusCode();
        var policy = (await created.Content.ReadFromJsonAsync<BackupPolicyDto>())!;
        using var requested = await client.PostAsJsonAsync(
            Route + "/policies/" + policy.Id + "/runs",
            new { idempotencyKey = "copy" });
        requested.EnsureSuccessStatusCode();
        var operation = (await requested.Content.ReadFromJsonAsync<BackupOperationDto>())!;
        var executor = scope.ServiceProvider.GetRequiredService<ExecuteBackupOperation>();
        Assert.True(await executor.ExecuteAsync(TestPlatformDefaults.TenantId, operation.Id, default));

        using var second = await client.PostAsJsonAsync(
            Route + "/destinations",
            input with { Name = "secondary", Prefix = "second" });
        second.EnsureSuccessStatusCode();
        var replacement = (await second.Content.ReadFromJsonAsync<BackupDestinationDto>())!;
        using var changed = await client.PutAsJsonAsync(
            Route + "/policies/" + policy.Id,
            new
            {
                policy.Name, policy.ResourceType, policy.ResourceId, destinationId = replacement.Id, policy.Schedule,
                policy.Suspended, policy.RetentionDays, expectedRevision = policy.Revision
            });
        changed.EnsureSuccessStatusCode();
        Assert.True(
            await scope.ServiceProvider
                .GetRequiredService<ApplyBackupPolicy>()
                .ExecuteAsync(TestPlatformDefaults.TenantId, policy.Id, default));
        await RunNow();
        var rows = mongo.Database.GetCollection<BackupOperation>("BackupOperations");
        Assert.Equal(BackupOperationState.Running, (await rows.Find(o => o.Id == operation.Id).SingleAsync()).State);
        var archiveRoute = Route + "/archives/" + policy.ArchiveId;
        using var busy = await client.DeleteAsync(archiveRoute + "?confirmed=true");
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);

        var histories = scope.ServiceProvider.GetRequiredService<ObserveBackupArchiveHistory>();
        Assert.True(await histories.ExecuteAsync(TestPlatformDefaults.TenantId, policy.ArchiveId, default));
        Assert.True(await histories.ExecuteAsync(TestPlatformDefaults.TenantId, policy.ArchiveId, default));
        provider.CopyObservation = new BackupCopyCompleted("copy-id");
        await RunNow();
        using var forgotten = await client.DeleteAsync(archiveRoute + "?confirmed=true");
        Assert.Equal(HttpStatusCode.NoContent, forgotten.StatusCode);
        Assert.Equal(0, provider.RunCount);

        async Task RunNow()
        {
            var operations = mongo.Database.GetCollection<BackupOperation>("BackupOperations");
            var current = await operations.Find(o => o.Id == operation.Id).SingleAsync();
            await operations.ReplaceOneAsync(
                o => o.Id == current.Id,
                current.Retry(DateTime.UtcNow.AddSeconds(-1), TimeSpan.Zero, null));
            Assert.True(await executor.ExecuteAsync(TestPlatformDefaults.TenantId, operation.Id, default));
        }
    }

    private static (ExecuteBackupCopy Execute, Func<int> CredentialCalls) CopyWithoutCredentialAccess(
        IServiceProvider services)
    {
        var calls = 0;
        var access = new BackupArchiveAccess(
            (_, _) => throw new InvalidOperationException("Native observation must not read storage credentials."),
            (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("Native observation must not resolve a projection.");
            });
        return (
            new ExecuteBackupCopy(
                services.GetRequiredService<BackupProviderCatalog>(),
                access,
                (_, _) => throw new InvalidOperationException("An existing native copy already reports its source.")),
            () => calls);
    }

    private static BackupArchive Archive(string resourceType) => BackupArchive.Create(
        Id(),
        TestPlatformDefaults.TenantId,
        Id(),
        Id(),
        new(resourceType, Id(), "source", 1, "17", 10, 1),
        new("https://storage.example", "bucket", "", Id(), "id", "key"),
        new(Id(), Id(), TestPlatformDefaults.DeploymentTargetId, Id(), "namespace", Id(), Id(), Id(), 1, Id(), 1),
        new BackupHistoryStart(Id(), new("trace", "correlation")),
        DateTime.UtcNow);

    private static BackupOperation Operation(BackupArchive archive) => BackupOperation.RequestCopy(
        Id(),
        archive.TenantId,
        "key",
        archive.PolicyId,
        archive.Id,
        DateTime.UtcNow,
        new("trace", "correlation"));

    private static string Id() => ObjectId.GenerateNewId().ToString();
}
