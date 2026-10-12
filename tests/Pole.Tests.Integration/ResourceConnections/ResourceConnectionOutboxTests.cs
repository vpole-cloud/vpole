using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceConnections.Application;
using Pole.Features.ResourceConnections.Application.Persistence;
using Pole.Features.ResourceConnections.Domain;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.ResourceConnections;

public sealed class ResourceConnectionOutboxTests(MongoReplicaSetFixture fixture) : MediatorIntegrationTestBase(fixture)
{
    private const string EventName = "pole.resource-connections/v1/resource-connection-changed";
    private IMongoCollection<ResourceConnection> Connections => Fixture.Database.GetCollection<ResourceConnection>("ResourceConnections");
    private IMongoCollection<ResourceConnectionCleanupTombstone> Tombstones => Fixture.Database.GetCollection<ResourceConnectionCleanupTombstone>("ResourceConnectionCleanupTombstones");

    [Fact]
    public async Task AcceptedWorkerResultsEmitWhileStaleGenerationRevisionTenantAndNoOpStayQuiet()
    {
        var connection = Connection();
        await Connections.InsertOneAsync(connection);
        var apply = Services.GetRequiredService<RecordResourceConnectionApply>();
        await apply(connection.TenantId, connection.Id, "target", connection.Generation, null);
        await apply(connection.TenantId, connection.Id, "target", connection.Generation, null);
        await apply(connection.TenantId, connection.Id, "target", connection.Generation - 1, "stale");
        await apply(ObjectId.GenerateNewId().ToString(), connection.Id, "target", connection.Generation, "foreign");
        var current = await Connections.Find(row => row.Id == connection.Id).SingleAsync();
        var verify = Services.GetRequiredService<RecordResourceConnectionReverificationFailure>();
        await verify(connection.TenantId, connection.Id, "target", current.Generation, current.Revision, "private-failure-detail");
        await verify(connection.TenantId, connection.Id, "target", current.Generation, current.Revision, "stale");
        var rows = await DbContext.OutboxMessages.Find(row => row.EventType == EventName).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => { Assert.Equal(connection.TenantId, row.TenantId); Assert.DoesNotContain("private-failure-detail", row.PayloadJson); });
    }

    [Fact]
    public async Task FinalCleanupAndPublicDeletionCommitHintsAndRetainCleanupTombstone()
    {
        var connection = Connection().BeginDeletion(DateTime.UtcNow);
        await Connections.InsertOneAsync(connection);
        var cleanup = Services.GetRequiredService<RecordResourceConnectionCleanup>();
        await cleanup(connection.TenantId, connection.Id, Location, connection.Generation, null);
        await cleanup(connection.TenantId, connection.Id, Location, connection.Generation, null);
        await Services.GetRequiredService<PurgeResourceConnectionIfComplete>()(connection);
        Assert.Equal(0, await Connections.CountDocumentsAsync(row => row.Id == connection.Id));
        Assert.Equal(1, await Tombstones.CountDocumentsAsync(row => row.Id == connection.Id));
        Assert.Equal(2, await DbContext.CountOutboxMessagesAsync(EventName));
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("cleanup")]
    [InlineData("verification")]
    [InlineData("cleanup-verification")]
    [InlineData("purge")]
    public async Task OutboxFailureRollsBackWorkerStateOrPurgeAndRetainedAuthority(string mutation)
    {
        var connection = Connection();
        if (mutation is "cleanup" or "cleanup-verification" or "purge")
            connection = connection.BeginDeletion(DateTime.UtcNow);
        if (mutation is "cleanup-verification" or "purge")
            connection = Assert.IsType<ConnectionCleanupRecorded>(connection.RecordCleanupCompleted(Location, connection.Generation, DateTime.UtcNow).Value).Connection;
        if (mutation == "verification")
            connection = Assert.IsType<ConnectionApplyRecorded>(connection.RecordApplied("target", connection.Generation, DateTime.UtcNow).Value).Connection;
        await Connections.InsertOneAsync(connection);
        var before = (await Fixture.Database.GetCollection<BsonDocument>("ResourceConnections").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync()).ToJson();
        await using var provider = FailingProvider();
        await using var scope = provider.CreateAsyncScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutation switch
        {
            "cleanup" => scope.ServiceProvider.GetRequiredService<RecordResourceConnectionCleanup>()(connection.TenantId, connection.Id, Location, connection.Generation, null),
            "verification" => scope.ServiceProvider.GetRequiredService<RecordResourceConnectionReverificationFailure>()(connection.TenantId, connection.Id, "target", connection.Generation, connection.Revision, "failure"),
            "cleanup-verification" => scope.ServiceProvider.GetRequiredService<RecordResourceConnectionCleanupVerificationFailure>()(connection.TenantId, connection.Id, Location, connection.Generation, connection.Revision, "failure"),
            "purge" => scope.ServiceProvider.GetRequiredService<PurgeResourceConnectionIfComplete>()(connection),
            _ => scope.ServiceProvider.GetRequiredService<RecordResourceConnectionApply>()(connection.TenantId, connection.Id, "target", connection.Generation, null)
        });
        Assert.Equal(before, (await Fixture.Database.GetCollection<BsonDocument>("ResourceConnections").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync()).ToJson());
        Assert.Equal(0, await Tombstones.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await DbContext.CountOutboxMessagesAsync(EventName));
    }

    private ServiceProvider FailingProvider()
    {
        var services = new ServiceCollection().AddSingleton(Fixture.Database).AddSingleton(Fixture.Client).AddSingleton<MongoRunTransaction>()
            .AddSingleton(TimeProvider.System).AddResourceConnectionsFeature();
        services.AddSingleton<StoreOutboxWrites>(async (session, writes, ct) =>
        {
            await Fixture.StoreOutboxWrites(session, writes, ct);
            throw new InvalidOperationException("Injected outbox failure.");
        });
        return services.BuildServiceProvider();
    }

    private static ConnectionTargetSnapshot Target => ConnectionTargetSnapshot.Create("target", "destination-deployment", "cell", "namespace", "service", 6379, 6379,
        [new ConnectionSourcePlacement("source-deployment", "cell", "namespace")]);
    private static ConnectionPolicyLocation Location => Target.PolicyLocation;
    private static ResourceConnection Connection() => ResourceConnectionFactory.Create(ObjectId.GenerateNewId().ToString(), TestData.TenantId, "connection",
        ResourceTypeId.Create("pole/web-service"), ObjectId.GenerateNewId().ToString(), ResourceTypeId.Create("pole/redis"), ObjectId.GenerateNewId().ToString(), "redis", [Target]);
}
