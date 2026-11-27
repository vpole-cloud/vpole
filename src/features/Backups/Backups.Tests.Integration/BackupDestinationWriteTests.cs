// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Backups.Application;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Destinations;
using Pole.Features.Backups.Domain;
using Pole.Features.Outbox.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Testing;
using Xunit;

namespace Pole.Features.Backups.Tests.Integration;

[Collection("backup-mongo")]
public sealed class BackupDestinationWriteTests(
    MongoReplicaSetFixture mongo
) : IAsyncLifetime
{
    private const string Route = "/api/v1/backups/destinations";
    private readonly BackupTestSetup setup = new(mongo);

    public Task InitializeAsync() => mongo.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Duplicate_name_reports_its_actual_index_and_aborts_registration_grant_and_outbox()
    {
        await using var host = await setup.StartAsync();
        using var client = BackupTestSetup.Client(host, TestPlatformDefaults.TenantId);
        var input = await BackupTestSetup.StorageAsync(client);
        using var created = await client.PostAsJsonAsync(Route, input);
        created.EnsureSuccessStatusCode();
        var outbox = mongo.Database.GetCollection<OutboxMessage>("OutboxMessages");
        var baseline = await outbox.CountDocumentsAsync(_ => true);
        await using var scope = host.Services.CreateAsyncScope();
        var writes = scope.ServiceProvider.GetRequiredService<BackupDestinationWrites>();
        var duplicateId = ObjectId.GenerateNewId().ToString();
        Assert.IsType<BackupDestinationNameAlreadyExists>(
            (await writes.CreateAsync(new(TestPlatformDefaults.TenantId, duplicateId, input), default)).Value);
        var registration = await scope.ServiceProvider.GetRequiredService<LoadRegisteredResource>()(
            new(TestPlatformDefaults.TenantId, BackupResourceTypes.Destination, duplicateId),
            default);
        Assert.Null(registration);
        var grant = await scope.ServiceProvider.GetRequiredService<ReadConsumerSecretValues>()(
            new(
                TestPlatformDefaults.TenantId,
                input.ResourceGroupId,
                input.IsolationScopeId,
                BackupResourceTypes.Destination,
                duplicateId,
                input.CredentialSecretId,
                [input.AccessKeyIdKey]),
            default);
        Assert.IsType<ConsumerSecretAccessDenied>(grant.Value);
        using var duplicate = await client.PostAsJsonAsync(Route, input);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(
            "backups.destination-name-exists",
            (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(
            1,
            await mongo.Database.GetCollection<BackupDestination>("BackupDestinations").CountDocumentsAsync(_ => true));
        Assert.Equal(baseline, await outbox.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task Noop_preserves_revision_and_fences_while_stale_or_changed_identity_has_a_specific_conflict()
    {
        await using var host = await setup.StartAsync();
        using var client = BackupTestSetup.Client(host, TestPlatformDefaults.TenantId);
        var input = await BackupTestSetup.StorageAsync(client);
        using var created = await client.PostAsJsonAsync(Route, input);
        created.EnsureSuccessStatusCode();
        var destination = (await created.Content.ReadFromJsonAsync<BackupDestinationDto>())!;
        await using var scope = host.Services.CreateAsyncScope();
        var writes = scope.ServiceProvider.GetRequiredService<BackupDestinationWrites>();
        var documents = mongo.Database.GetCollection<BackupDestination>("BackupDestinations");
        var before = await documents.Find(d => d.Id == destination.Id).SingleAsync();
        var outbox = mongo.Database.GetCollection<OutboxMessage>("OutboxMessages");
        var baseline = await outbox.CountDocumentsAsync(_ => true);
        var command = new UpdateBackupDestinationCommand(
            TestPlatformDefaults.TenantId,
            destination.Id,
            destination.Revision,
            input);
        Assert.Equal(before, Assert.IsType<BackupDestination>((await writes.UpdateAsync(command, default)).Value));
        Assert.IsType<BackupDestinationRevisionConflict>(
            (await writes.UpdateAsync(command with { ExpectedRevision = 2 }, default)).Value);
        Assert.IsType<BackupDestinationIdentityImmutable>(
            (await writes.UpdateAsync(command with { Input = input with { Bucket = "other-bucket" } }, default)).Value);
        Assert.Equal(before, await documents.Find(d => d.Id == destination.Id).SingleAsync());
        Assert.Equal(baseline, await outbox.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task Unavailable_credentials_reject_creation_and_abort_the_resource_registration()
    {
        await using var host = await setup.StartAsync();
        using var client = BackupTestSetup.Client(host, TestPlatformDefaults.TenantId);
        var input = await BackupTestSetup.StorageAsync(client);
        input = input with { CredentialSecretId = ObjectId.GenerateNewId().ToString() };
        await using var scope = host.Services.CreateAsyncScope();
        var writes = scope.ServiceProvider.GetRequiredService<BackupDestinationWrites>();
        var id = ObjectId.GenerateNewId().ToString();
        Assert.IsType<BackupDestinationCredentialGrantDenied>(
            (await writes.CreateAsync(new(TestPlatformDefaults.TenantId, id, input), default)).Value);
        Assert.Null(
            await scope.ServiceProvider.GetRequiredService<LoadRegisteredResource>()(
                new(TestPlatformDefaults.TenantId, BackupResourceTypes.Destination, id),
                default));
        Assert.Equal(
            0,
            await mongo.Database.GetCollection<BackupDestination>("BackupDestinations").CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task An_unrelated_unique_index_failure_keeps_the_standard_exception_path()
    {
        await using var host = await setup.StartAsync();
        using var client = BackupTestSetup.Client(host, TestPlatformDefaults.TenantId);
        var input = await BackupTestSetup.StorageAsync(client);
        using var created = await client.PostAsJsonAsync(Route, input);
        created.EnsureSuccessStatusCode();
        var documents = mongo.Database.GetCollection<BackupDestination>("BackupDestinations");
        await documents.Indexes.CreateOneAsync(
            new CreateIndexModel<BackupDestination>(
                Builders<BackupDestination>.IndexKeys.Ascending(d => d.TenantId).Ascending(d => d.ResourceGroupId),
                new CreateIndexOptions { Name = "UX_tests_Destination_Group", Unique = true }));
        await using var scope = host.Services.CreateAsyncScope();
        var writes = scope.ServiceProvider.GetRequiredService<BackupDestinationWrites>();
        var error = await Assert.ThrowsAsync<MongoWriteException>(() => writes.CreateAsync(
            new(TestPlatformDefaults.TenantId, ObjectId.GenerateNewId().ToString(), input with { Name = "secondary" }),
            default));
        Assert.Equal(ServerErrorCategory.DuplicateKey, error.WriteError.Category);
        Assert.Equal(1, await documents.CountDocumentsAsync(_ => true));
    }
}
