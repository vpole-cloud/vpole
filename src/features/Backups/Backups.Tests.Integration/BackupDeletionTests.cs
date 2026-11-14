using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Application;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Destinations;
using Pole.Features.Backups.Application.Policies;
using Pole.Features.Backups.Domain;
using Pole.Features.Outbox.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Testing;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.Backups.Tests.Integration;

[Collection("backup-mongo")]
public sealed class BackupDeletionTests(
    MongoReplicaSetFixture mongo
) : IAsyncLifetime
{
    private const string Route = "/api/v1/backups";
    private readonly BackupTestSetup setup = new(mongo);

    public Task InitializeAsync() => mongo.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Concurrent_policy_update_and_deletion_accept_only_one_revision()
    {
        await using var host = await setup.StartAsync();
        using var client = BackupTestSetup.Client(host, TestPlatformDefaults.TenantId);
        var storage = await BackupTestSetup.StorageAsync(client);
        using var destinationResponse = await client.PostAsJsonAsync(Route + "/destinations", storage);
        destinationResponse.EnsureSuccessStatusCode();
        var destination = (await destinationResponse.Content.ReadFromJsonAsync<BackupDestinationDto>())!;
        await using var scope = host.Services.CreateAsyncScope();
        var type = ResourceTypeId.Create(host.Services.GetRequiredService<TestBackupProvider>().ResourceType);
        var sourceId = Id();
        await setup.SeedSourceAsync(scope.ServiceProvider, storage, type, sourceId, Id());
        using var created = await client.PostAsJsonAsync(
            Route + "/policies",
            new BackupPolicyInput("daily", type.Value, sourceId, destination.Id, "0 2 * * *", false, 30));
        created.EnsureSuccessStatusCode();
        var policy = (await created.Content.ReadFromJsonAsync<BackupPolicyDto>())!;
        var outbox = mongo.Database.GetCollection<OutboxMessage>("OutboxMessages");
        var baseline = await outbox.CountDocumentsAsync(_ => true);

        var responses = await Task.WhenAll(
            client.PutAsJsonAsync(
                Route + "/policies/" + policy.Id,
                new
                {
                    policy.Name, policy.ResourceType, policy.ResourceId, policy.DestinationId, schedule = "0 3 * * *",
                    policy.Suspended, policy.RetentionDays, expectedRevision = policy.Revision
                }),
            client.DeleteAsync(Route + "/policies/" + policy.Id + "?expectedRevision=" + policy.Revision));
        using var winner = Assert.Single(responses, response => response.IsSuccessStatusCode);
        using var loser = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(
            "backups.revision-conflict",
            (await loser.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var saved = await mongo.Database
            .GetCollection<BackupPolicy>("BackupPolicies")
            .Find(p => p.Id == policy.Id)
            .SingleAsync();
        Assert.Equal(2, saved.Revision);
        Assert.Equal(winner.StatusCode == HttpStatusCode.NoContent, saved.Deleting);
        Assert.Equal(baseline + 2, await outbox.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task Policy_stop_and_its_work_request_abort_together()
    {
        await using var host = await setup.StartAsync(failPolicy: true);
        var policy = BackupPolicyFactory.Create(
            Id(),
            TestPlatformDefaults.TenantId,
            "daily",
            "pole/test-backup-database",
            Id(),
            Id(),
            Id(),
            "0 2 * * *",
            false,
            30,
            new("origin-trace", "origin-correlation"),
            DateTime.UtcNow);
        var policies = mongo.Database.GetCollection<BackupPolicy>("BackupPolicies");
        await policies.InsertOneAsync(policy);
        var before = await policies.Find(p => p.Id == policy.Id).SingleAsync();
        var outbox = mongo.Database.GetCollection<OutboxMessage>("OutboxMessages");
        var baseline = await outbox.CountDocumentsAsync(_ => true);
        await using var scope = host.Services.CreateAsyncScope();
        var deletion = scope.ServiceProvider.GetRequiredService<BeginBackupPolicyDeletion>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => deletion.ExecuteAsync(
            new(policy.TenantId, policy.Id, policy.Revision),
            default));
        Assert.Equal(before, await policies.Find(p => p.Id == policy.Id).SingleAsync());
        Assert.Equal(baseline, await outbox.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task Destination_removal_registration_and_notification_abort_together()
    {
        await using var host = await setup.StartAsync(failDestination: true);
        using var client = BackupTestSetup.Client(host, TestPlatformDefaults.TenantId);
        var input = await BackupTestSetup.StorageAsync(client);
        var destination = BackupDestination.Create(
            Id(),
            TestPlatformDefaults.TenantId,
            input.Name,
            input.ResourceGroupId,
            input.IsolationScopeId,
            input.Storage());
        var destinations = mongo.Database.GetCollection<BackupDestination>("BackupDestinations");
        await destinations.InsertOneAsync(destination);
        await using var scope = host.Services.CreateAsyncScope();
        var registrar = scope.ServiceProvider.GetRequiredService<IResourceRegistrar>();
        await scope.ServiceProvider
            .GetRequiredService<MongoRunTransaction>()
            .InvokeOutcomeAsync<bool>(async (transaction, token) =>
            {
                var registered = await registrar.RegisterAsync(
                    new(
                        destination.TenantId,
                        destination.ResourceGroupId,
                        new(BackupResourceTypes.Destination, destination.Id, destination.Name)),
                    transaction.Session,
                    token);
                Assert.IsType<RegisteredResource>(registered.Value);
                return transaction.Commit(true);
            });
        var outbox = mongo.Database.GetCollection<OutboxMessage>("OutboxMessages");
        var baseline = await outbox.CountDocumentsAsync(_ => true);
        var deletion = scope.ServiceProvider.GetRequiredService<DeleteBackupDestination>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => deletion.ExecuteAsync(
            new(destination.TenantId, destination.Id, destination.Revision),
            default));
        Assert.Equal(destination, await destinations.Find(d => d.Id == destination.Id).SingleAsync());
        Assert.NotNull(
            await scope.ServiceProvider.GetRequiredService<LoadRegisteredResource>()(
                new(destination.TenantId, BackupResourceTypes.Destination, destination.Id),
                default));
        Assert.Equal(baseline, await outbox.CountDocumentsAsync(_ => true));
    }

    private static string Id() => ObjectId.GenerateNewId().ToString();
}
