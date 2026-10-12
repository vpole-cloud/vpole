using System.Text.Json;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Endpoints;
using Pole.Features.ComputeProfiles.Application.Persistence.Mutations;
using Pole.Features.ComputeProfiles.Application.Realtime;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.ComputeProfiles.Events;
using Pole.Features.DeploymentTargets.Application.Persistence.References;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Application.Persistence.Writes;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Realtime.Contracts;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ComputeProfiles;

[Collection(IntegrationTestCollection.Name)]
public sealed class ComputeProfileRealtimeOutboxTests(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    private const string EventType = "pole.compute-profiles/v1/compute-profile-catalog-changed";
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AcceptedCreateUpdateAndDeletePersistMinimalCatalogFacts()
    {
        var target = ComputeProfileTestData.CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        using var host = new ComputeProfilePersistenceTestHost(fixture);
        var profile = ComputeProfileTestData.CreateProfile(target.Id);
        Assert.IsType<ComputeProfile>((await host.GetRequiredService<PersistCreatedComputeProfile>()(profile)).Value);
        var replacement = profile with { DisplayName = "Private changed compute name", Revision = 2, UpdatedAtUtc = profile.UpdatedAtUtc.AddSeconds(1) };
        Assert.IsType<ComputeProfile>((await host.GetRequiredService<PersistUpdatedComputeProfile>()(profile, replacement)).Value);
        Assert.IsType<ComputeProfileDeleted>((await host.GetRequiredService<DeleteComputeProfile>()(profile.Id, 2)).Value);
        var messages = await fixture.DbContext.OutboxMessages.Find(message => message.EventType == EventType).ToListAsync();
        Assert.Equal(3, messages.Count);
        foreach (var message in messages)
        {
            Assert.Null(message.TenantId);
            var payload = JsonSerializer.Deserialize<ComputeProfileCatalogChangedEvent>(message.PayloadJson, EventExtensions.SerializerOptions)!;
            Assert.Equal(profile.Id, payload.ComputeProfileId);
            Assert.Equal("", payload.EventCore.TenantId);
            using var json = JsonDocument.Parse(message.PayloadJson);
            Assert.Equal(new[] { "computeProfileId", "eventCore", "revision" },
                json.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
            Assert.DoesNotContain(profile.DisplayName, message.PayloadJson);
            Assert.DoesNotContain(replacement.DisplayName, message.PayloadJson);
            Assert.DoesNotContain(target.Id, message.PayloadJson);
        }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task OutboxFailureRollsBackProfileDefaultsAndTargetReferences(string operation)
    {
        var firstTarget = ComputeProfileTestData.CreateTarget();
        var secondTarget = ComputeProfileTestData.CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertManyAsync([firstTarget, secondTarget]);
        using var host = new ComputeProfilePersistenceTestHost(fixture);
        var originalDefault = ComputeProfileTestData.CreateProfile(firstTarget.Id, isDefault: true);
        Assert.IsType<ComputeProfile>((await host.GetRequiredService<PersistCreatedComputeProfile>()(originalDefault)).Value);
        var candidate = ComputeProfileTestData.CreateProfile(secondTarget.Id);
        if (operation != "create")
            Assert.IsType<ComputeProfile>((await host.GetRequiredService<PersistCreatedComputeProfile>()(candidate)).Value);
        var profiles = fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles");
        var beforeCount = await CountEventsAsync();
        var beforeReferences = await fixture.DbContext.DeploymentTargetReferences.Find(_ => true).ToListAsync();
        var beforeTargets = await fixture.DbContext.DeploymentTargets.Find(_ => true).ToListAsync();
        var realStore = new MongoStoreOutboxWrites(fixture.Database);
        StoreOutboxWrites failingStore = async (session, writes, ct) =>
        {
            await realStore.InvokeAsync(session, writes, ct);
            throw new InvalidOperationException("controlled catalog outbox failure");
        };
        using var failingHost = new ComputeProfilePersistenceTestHost(fixture, storeOutboxWrites: failingStore);
        switch (operation)
        {
            case "create":
                await Assert.ThrowsAsync<InvalidOperationException>(() => failingHost.GetRequiredService<PersistCreatedComputeProfile>()(candidate with { IsDefault = true }));
                Assert.False(await profiles.Find(profile => profile.Id == candidate.Id).AnyAsync());
                break;
            case "update":
                var replacement = candidate with { IsDefault = true, TargetMappings = originalDefault.TargetMappings, Revision = 2, UpdatedAtUtc = candidate.UpdatedAtUtc.AddSeconds(1) };
                await Assert.ThrowsAsync<InvalidOperationException>(() => failingHost.GetRequiredService<PersistUpdatedComputeProfile>()(candidate, replacement));
                break;
            case "delete":
                await Assert.ThrowsAsync<InvalidOperationException>(() => failingHost.GetRequiredService<DeleteComputeProfile>()(candidate.Id, candidate.Revision));
                break;
        }
        if (operation != "create")
        {
            var persisted = await profiles.Find(profile => profile.Id == candidate.Id).SingleAsync();
            Assert.Equal(1, persisted.Revision);
            Assert.False(persisted.IsDefault);
            Assert.Equal(secondTarget.Id, Assert.Single(persisted.TargetMappings).DeploymentTargetId);
        }
        var activeDefault = await profiles.Find(profile => profile.Id == originalDefault.Id).SingleAsync();
        Assert.True(activeDefault.IsDefault);
        Assert.Equal(1, activeDefault.Revision);
        var afterReferences = await fixture.DbContext.DeploymentTargetReferences.Find(_ => true).ToListAsync();
        Assert.Equal(beforeReferences.Select(reference => reference.Id).Order(), afterReferences.Select(reference => reference.Id).Order());
        foreach (var before in beforeTargets)
            Assert.Equal(before.ReferenceFence, (await fixture.DbContext.DeploymentTargets.Find(target => target.Id == before.Id).SingleAsync()).ReferenceFence);
        Assert.Equal(beforeCount, await CountEventsAsync());
    }

    [Fact]
    public async Task RejectedAdmissionAndStaleUpdateAppendNoCatalogEvent()
    {
        var target = ComputeProfileTestData.CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        using var host = new ComputeProfilePersistenceTestHost(fixture);
        Assert.IsType<Pole.Features.DeploymentTargets.Contracts.DeploymentTargetReferenceChangeRejected>((await
            host.GetRequiredService<PersistCreatedComputeProfile>()(ComputeProfileTestData.CreateProfile(MongoDB.Bson.ObjectId.GenerateNewId().ToString()))).Value);
        Assert.Equal(0, await CountEventsAsync());
        var profile = ComputeProfileTestData.CreateProfile(target.Id);
        await host.GetRequiredService<PersistCreatedComputeProfile>()(profile);
        var updated = profile with { DisplayName = "Accepted", Revision = 2, UpdatedAtUtc = profile.UpdatedAtUtc.AddSeconds(1) };
        await host.GetRequiredService<PersistUpdatedComputeProfile>()(profile, updated);
        Assert.IsType<ComputeProfileRevisionConflict>((await host.GetRequiredService<PersistUpdatedComputeProfile>()(profile,
            updated with { DisplayName = "Stale" })).Value);
        Assert.Equal(2, await CountEventsAsync());
    }

    [Fact]
    public async Task ConsumerSeparatesAdminCatalogIdentityFromPublicEligibility()
    {
        var changes = new List<RealtimeChange>();
        var publicTopics = new List<string>();
        var consumer = new NotifyComputeProfileCatalogChangedConsumer((change, _) => { changes.Add(change); return Task.CompletedTask; },
            (topic, _) => { publicTopics.Add(topic); return Task.CompletedTask; });
        await consumer.HandleAsync(new ComputeProfileCatalogChangedEvent("private-profile", 4));
        var hint = Assert.Single(changes);
        Assert.Equal("compute-profiles", hint.Topic);
        Assert.Null(hint.TenantId);
        Assert.Equal("private-profile", hint.ResourceId);
        Assert.Equal(4, hint.Revision);
        Assert.Equal("compute-eligibility", Assert.Single(publicTopics));
    }

    private Task<long> CountEventsAsync() => fixture.DbContext.CountOutboxMessagesAsync(EventType);
}
