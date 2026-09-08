using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.RedisServices.Application.Migrations;
using Pole.Features.RedisServices.Domain;
using Pole.Features.Shared.Models;
using Pole.Features.RedisServices.Tests.Integration.Infrastructure;
using Pole.Testing;
using Xunit;

namespace Pole.Features.RedisServices.Tests.Integration.RedisServices;

[Collection(IntegrationTestCollection.Name)]
public sealed class RedisSchemaValidationTests(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    private const string CollectionName = "RedisServices";

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Migration_creates_collection_that_accepts_current_lifecycle_shapes()
    {
        await new RedisSchemaValidationMigration().Up(fixture.Database);
        var contract = CreateContract();

        await contract.AssertStrictErrorValidationAsync();
        await contract.AssertAcceptsAsync(
        [
            CreateRedis(new ActiveRedisLifecycle()),
            CreateRedis(new DeletingRedisLifecycle(DateTime.UtcNow))
        ]);
    }

    [Fact]
    public async Task Migration_updates_existing_collection_and_rejects_missing_tenant_scope()
    {
        await fixture.Database.CreateCollectionAsync(CollectionName);
        await new RedisSchemaValidationMigration().Up(fixture.Database);
        var contract = CreateContract();

        await contract.AssertStrictErrorValidationAsync();
        await contract.AssertRejectsAsync(
            CreateRedis(new ActiveRedisLifecycle()),
            document => document.Remove(nameof(Redis.TenantId)));
    }

    [Fact]
    public async Task Validator_rejects_deleting_lifecycle_without_requested_timestamp()
    {
        await new RedisSchemaValidationMigration().Up(fixture.Database);

        await CreateContract().AssertRejectsAsync(
            CreateRedis(new DeletingRedisLifecycle(DateTime.UtcNow)),
            document => document[nameof(Redis.Lifecycle)].AsBsonDocument.Remove(
                nameof(DeletingRedisLifecycle.RequestedAtUtc)));
    }

    [Fact]
    public async Task Validator_rejects_legacy_direct_compute_resources()
    {
        await new RedisSchemaValidationMigration().Up(fixture.Database);

        await CreateContract().AssertRejectsAsync(
            CreateRedis(new ActiveRedisLifecycle()),
            document => document[nameof(Redis.Spec)].AsBsonDocument["Resources"] =
                new BsonDocument());
    }

    private MongoDocumentContractTester<Redis> CreateContract() =>
        new(fixture.Database, CollectionName);

    private static Redis CreateRedis(RedisLifecycle lifecycle) => RedisFactory.Create(
        RedisTestData.TenantId,
        "cache",
        ObjectId.GenerateNewId().ToString(),
        new RedisSpec(
            new Storage(5, RedisTestData.DefaultStorageProfileId)),
        RedisFactory.DefaultCreateOptions with
        {
            Id = ObjectId.GenerateNewId().ToString(),
            Tags = ImmutableHashSet.Create("development"),
            Events = ImmutableList.Create(new ServiceEvent(DateTime.UtcNow, "created")),
            Lifecycle = lifecycle
        });
}
