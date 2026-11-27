// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.Realtime.Contracts;
using Pole.Features.Search.Application;
using Pole.Features.Search.Application.Realtime;
using Pole.Features.Search.Contracts;
using Pole.Features.Search.Events;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Search;

[Collection(IntegrationTestCollection.Tenant4)]
public sealed class SearchProjectionOutboxTests(
    MongoReplicaSetFixture fixture
) : MediatorIntegrationTestBase(fixture)
{
    private const string EventName = "pole.search/v1/search-projection-changed";
    private static readonly ResourceTypeId Type = ResourceTypeId.Create("third-party/widget");

    [Fact]
    public async Task OwnProjectionCommitEmitsHintsWhileStaleEqualAndDeletedUpdatesStayQuiet()
    {
        var snapshot = Snapshot();
        var create = Services.GetRequiredService<CreateSearchProjection>();
        var status = Services.GetRequiredService<UpdateSearchOperationalStatus>();
        var delete = Services.GetRequiredService<DeleteSearchProjection>();
        await create(snapshot);
        await create(snapshot);
        var current = new SearchOperationalStatusSnapshot(1, 2, "Failed", "private-failure-detail", DateTime.UtcNow);
        await status(snapshot.Identity, current);
        await status(snapshot.Identity, current);
        await status(
            snapshot.Identity,
            current with
            {
                ObservedGeneration = 1,
            });
        await delete(snapshot.Identity, 2);
        await delete(snapshot.Identity, 2);
        await create(snapshot);
        await status(snapshot.Identity, current);
        var envelopes = await DbContext.OutboxMessages
            .Find(row => row.TenantId == snapshot.Identity.TenantId && row.EventType == EventName)
            .ToListAsync();
        Assert.Equal(3, envelopes.Count);
        var messages = envelopes.Select(row =>
                JsonSerializer.Deserialize<SearchProjectionChangedEvent>(
                    row.PayloadJson,
                    EventExtensions.SerializerOptions) !)
            .ToArray();
        Assert.All(
            messages,
            message =>
            {
                Assert.Equal(snapshot.Identity.TenantId, message.EventCore.TenantId);
                Assert.Equal(snapshot.Identity.SourceId, message.SourceId);
                Assert.Equal(Type.Value, message.ResourceType);
            });
        Assert.Single(messages, message => message.ObservedGeneration == 2);
        Assert.All(
            envelopes,
            row =>
            {
                Assert.DoesNotContain(snapshot.Title, row.PayloadJson);
                Assert.DoesNotContain("private-failure-detail", row.PayloadJson);
            });
        var notices = new List<RealtimeChange>();
        var consumer = new NotifySearchProjectionChangedConsumer((notice, _) =>
        {
            notices.Add(notice);
            return Task.CompletedTask;
        });
        foreach (var message in messages)
            await consumer.HandleAsync(message, default);
        Assert.All(notices, notice => Assert.Equal(new RealtimeChange("search", snapshot.Identity.TenantId), notice));
    }

    [Fact]
    public async Task StatusBeforeMetadataCommitsPendingProjectionAndSubsequentSnapshotSeparately()
    {
        var snapshot = Snapshot();
        var status = Services.GetRequiredService<UpdateSearchOperationalStatus>();
        await status(snapshot.Identity, new SearchOperationalStatusSnapshot(2, 3, "Applied", null, DateTime.UtcNow));
        await Services.GetRequiredService<CreateSearchProjection>()(snapshot);
        Assert.Equal(2, await DbContext.CountOutboxMessagesAsync(Data.TenantId, EventName));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("status")]
    [InlineData("pending-status")]
    public async Task OutboxFailureRestoresAllProjectionCollectionsAndOutbox(string mutation)
    {
        var snapshot = Snapshot();
        if (mutation is not ("create" or "pending-status"))
            await Services.GetRequiredService<CreateSearchProjection>()(snapshot);
        var before = await ProjectionState();
        var count = await DbContext.CountOutboxMessagesAsync(Data.TenantId, EventName);
        var services = new ServiceCollection().AddSingleton(Fixture.Client)
            .AddSingleton(Fixture.Database)
            .AddSingleton(new SearchSourceType(Type, "Widget", 3))
            .AddSearchFeature();
        services.AddSingleton<StoreOutboxWrites>(async (
            session, writes,
            ct) =>
        {
            Assert.True(session.IsInTransaction);
            await Fixture.StoreOutboxWrites(session, writes, ct);
            Assert.Equal(count, await DbContext.CountOutboxMessagesAsync(Data.TenantId, EventName));
            throw new InvalidOperationException("Injected outbox failure.");
        });
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutation switch
        {
            "delete" => scope.ServiceProvider.GetRequiredService<DeleteSearchProjection>()(snapshot.Identity, 2),
            "status" or "pending-status" => scope.ServiceProvider.GetRequiredService<UpdateSearchOperationalStatus>()(
                snapshot.Identity,
                new SearchOperationalStatusSnapshot(2, 2, "Applied", null, DateTime.UtcNow)),
            _ => scope.ServiceProvider.GetRequiredService<CreateSearchProjection>()(
                snapshot with
                {
                    ResourceRevision = 2,
                    Title = "replacement",
                }),
        });
        Assert.Equal(before, await ProjectionState());
        Assert.Equal(count, await DbContext.CountOutboxMessagesAsync(Data.TenantId, EventName));
    }

    [Fact]
    public void SearchRegistersOneTenantTopicWithoutConcreteProviders()
    {
        var services = new ServiceCollection().AddSearchFeature();
        Assert.Equal(
            new RealtimeTopic("search", RealtimeScope.Tenant),
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(RealtimeTopic))
                .ImplementationInstance);
    }

    private async Task<string[]> ProjectionState()
    {
        var rows = new List<string>();
        foreach (var collection in new[] { "SearchDocuments", "SearchSourceDocuments", "SearchProjectionVersions" })
            rows.Add(
                (await Fixture.Database
                    .GetCollection<BsonDocument>(collection)
                    .Find(Builders<BsonDocument>.Filter.Eq("TenantId", Data.TenantId))
                    .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                    .ToListAsync()).ToJson());
        return rows.ToArray();
    }

    private SearchProjectionSnapshot Snapshot() => new(
        new SearchProjectionIdentity(Data.TenantId, Type, ObjectId.GenerateNewId().ToString()),
        "private-search-title",
        null,
        null,
        [new SearchProjectionField(SearchProjectionFieldType.Name, "private-search-title")],
        1);
}
