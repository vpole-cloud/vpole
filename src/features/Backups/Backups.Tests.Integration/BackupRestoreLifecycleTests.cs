// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Operations;
using Pole.Features.Backups.Application.Providers;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Testing;
using Xunit;

namespace Pole.Features.Backups.Tests.Integration;

[Collection("backup-mongo")]
public sealed class BackupRestoreLifecycleTests(
    MongoReplicaSetFixture mongo
) : IAsyncLifetime
{
    private readonly BackupTestSetup setup = new(mongo);

    public Task InitializeAsync() => mongo.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_target_progress_does_not_require_catalog_credentials_or_projection(bool completed)
    {
        await using var host = await setup.StartAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var provider = host.Services.GetRequiredService<TestBackupProvider>();
        var archive = Archive(provider.ResourceType);
        var operation = Operation(archive);
        var targetId = ((RestoreBackupWork)operation.Work).Resource.Id;
        operation = operation.RecordRestoreTarget(targetId, DateTime.UtcNow);
        provider.RestoreObservation = completed ? new BackupRestoreCompleted(targetId) : new BackupRestoreInProgress();
        var execute = WithoutCredentials(scope.ServiceProvider);
        var apply = await execute.ExecuteAsync(operation, archive, default);
        var updated = apply(operation, DateTime.UtcNow);
        Assert.Equal(completed ? BackupOperationState.Succeeded : BackupOperationState.Running, updated.State);
        Assert.Equal(0, provider.CatalogCount);
        Assert.Null(provider.RestoreStorageGiB);
    }

    [Fact]
    public async Task A_recorded_target_is_never_recreated_after_it_disappears()
    {
        await using var host = await setup.StartAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var provider = host.Services.GetRequiredService<TestBackupProvider>();
        var archive = Archive(provider.ResourceType);
        var operation = Operation(archive);
        var targetId = ((RestoreBackupWork)operation.Work).Resource.Id;
        operation = operation.RecordRestoreTarget(targetId, DateTime.UtcNow);
        provider.RestoreObservation = new BackupRestoreTargetMissing();
        var execute = WithoutCredentials(scope.ServiceProvider);
        var apply = await execute.ExecuteAsync(operation, archive, default);
        var updated = apply(operation, DateTime.UtcNow);
        Assert.Equal(BackupOperationState.Failed, updated.State);
        Assert.Equal("restore-resource-deleted", updated.FailureCode);
        var failed = Assert.IsType<FailedBackupRestore>(Assert.IsType<RestoreBackupWork>(updated.Work).Execution);
        Assert.Equal(targetId, Assert.IsType<RecordedRestoreTarget>(failed.Target).ResourceId);
        Assert.Equal(0, provider.CatalogCount);
    }

    [Fact]
    public async Task Restore_success_must_identify_the_current_recorded_resource()
    {
        await using var host = await setup.StartAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var provider = host.Services.GetRequiredService<TestBackupProvider>();
        var archive = Archive(provider.ResourceType);
        var operation = Operation(archive);
        operation = operation.RecordRestoreTarget(((RestoreBackupWork)operation.Work).Resource.Id, DateTime.UtcNow);
        provider.RestoreObservation = new BackupRestoreCompleted(Id());
        var apply = await WithoutCredentials(scope.ServiceProvider).ExecuteAsync(operation, archive, default);
        Assert.Throws<InvalidOperationException>(() => apply(operation, DateTime.UtcNow));
        Assert.Equal(BackupOperationState.Running, operation.State);
    }

    private static ExecuteBackupRestore WithoutCredentials(IServiceProvider services)
    {
        var access = new BackupArchiveAccess(
            (_, _) => throw new InvalidOperationException("Observation must not read credentials."),
            (_, _) => throw new InvalidOperationException("Observation must not resolve projections."));
        var providers = services.GetRequiredService<BackupProviderCatalog>();
        var provider = services.GetRequiredService<TestBackupProvider>();
        provider.CatalogResponse = new BackupCatalogUnavailable();
        provider.CreateTarget = (_, _) => throw new InvalidOperationException("Observation must not create a target.");
        return new(providers, access, new VerifyBackupRestoreTarget(providers, access, TimeProvider.System));
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

    private static BackupOperation Operation(BackupArchive archive) => BackupOperation.RequestRestore(
        Id(),
        archive.TenantId,
        "key",
        archive.PolicyId,
        archive.Id,
        new BackupCopySelection("copy"),
        new(Id(), "restored", null),
        DateTime.UtcNow,
        new("trace", "correlation"));

    private static string Id() => ObjectId.GenerateNewId().ToString();
}
