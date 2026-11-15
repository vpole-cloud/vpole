using System.Collections.Immutable;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Outbox.Application;
using Pole.Features.Outbox.Domain;
using Pole.Features.SeaweedFS.Application;
using Pole.Features.SeaweedFS.Application.Services;
using Pole.Features.SeaweedFS.Domain;
using Pole.Features.SeaweedFS.Events;
using Pole.Features.SeaweedFS.Migrations;
using Pole.Testing;
using Xunit;

namespace Pole.Features.SeaweedFS.Tests.Integration;

[CollectionDefinition("seaweedfs-mongo")]
public sealed class SeaweedFSMongoCollection : ICollectionFixture<MongoReplicaSetFixture>;

[Collection("seaweedfs-mongo")]
public sealed class SeaweedFSPersistenceTests(
    MongoReplicaSetFixture fixture
) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Create_and_event_commit_together_and_abort_together()
    {
        await new SeaweedFSSchemaMigration().Up(fixture.Database);
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(fixture.Database);
        services.AddOutboxFeature(new ConfigurationBuilder().Build(), typeof(SeaweedFSServiceCreatedEvent).Assembly);
        services.AddSeaweedFSFeature();
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var persist = scope.ServiceProvider.GetRequiredService<PersistCreatedSeaweedFS>();
        var load = scope.ServiceProvider.GetRequiredService<LoadSeaweedFSById>();
        var tenant = ObjectId.GenerateNewId().ToString();

        SeaweedFSService Create(string name) => SeaweedFSFactory.Create(
            tenant,
            name,
            ObjectId.GenerateNewId().ToString(),
            new(
                SeaweedFSMode.SingleNode,
                new(10, ObjectId.GenerateNewId().ToString()),
                new EmbeddedSeaweedFSMetadata()),
            ImmutableHashSet<string>.Empty,
            ObjectId.GenerateNewId().ToString(),
            new(100, 128, 4, 12));

        var committed = Create("committed");
        using (var session = await fixture.Client.StartSessionAsync())
        {
            session.StartTransaction();
            Assert.IsType<SeaweedFSServicePersisted>((await persist(session, committed)).Value);
            await session.CommitTransactionAsync();
        }

        var aborted = Create("aborted");
        using (var session = await fixture.Client.StartSessionAsync())
        {
            session.StartTransaction();
            await persist(session, aborted);
            await session.AbortTransactionAsync();
        }

        Assert.NotNull(await load(tenant, committed.Id));
        Assert.Null(await load(tenant, aborted.Id));
        Assert.Null(await load(ObjectId.GenerateNewId().ToString(), committed.Id));
        var messages = await fixture.Database
            .GetCollection<OutboxMessage>("OutboxMessages")
            .Find(_ => true)
            .ToListAsync();
        Assert.Equal(tenant, Assert.Single(messages).TenantId);
        Assert.Equal("pole.seaweedfs/v1/seaweedfs-service-created", messages[0].EventType);
    }
}
