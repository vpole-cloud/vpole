using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Realtime.Contracts;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Persistence.Mutations;
using Pole.Features.StorageProfiles.Application.Realtime;
using Pole.Features.StorageProfiles.Domain;
using Pole.Features.StorageProfiles.Events;
using Pole.Observability;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.StorageProfiles;

public sealed class StorageProfileRealtimeOutboxTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private const string EventType = "pole.storage-profiles/v1/storage-profile-catalog-changed";

    [Fact]
    public async Task AcceptedCreateAndUpdatePersistOnlyCatalogIdentityAndRevisionWithTrace()
    {
        var candidate = Candidate();
        using var trace = TraceContext.Push(new("0123456789abcdef0123456789abcdef", "storage-catalog-realtime"));
        var create = Services.GetRequiredService<PersistCreatedStorageProfile>();
        Assert.Equal(candidate.Id, (await create(candidate)).Id);
        var replacement = candidate with
        {
            DisplayName = "Changed private catalog name",
            Revision = 2,
            UpdatedAtUtc = candidate.UpdatedAtUtc.AddSeconds(1)
        };
        var update = Services.GetRequiredService<PersistUpdatedStorageProfile>();
        Assert.IsType<StorageProfile>((await update(candidate, replacement)).Value);
        var messages = await DbContext.OutboxMessages.Find(message => message.EventType == EventType).ToListAsync();
        Assert.Equal(2, messages.Count);
        Assert.Equal(new long[] { 1, 2 },
            messages.Select(message =>
                JsonSerializer.Deserialize<StorageProfileCatalogChangedEvent>(message.PayloadJson,
                    EventExtensions.SerializerOptions)!.Revision).Order().ToArray());
        foreach (var message in messages)
        {
            Assert.Null(message.TenantId);
            Assert.Equal("0123456789abcdef0123456789abcdef", message.TraceId);
            Assert.Equal("storage-catalog-realtime", message.CorrelationId);
            var payload =
                JsonSerializer.Deserialize<StorageProfileCatalogChangedEvent>(message.PayloadJson,
                    EventExtensions.SerializerOptions)!;
            Assert.Equal(candidate.Id, payload.StorageProfileId);
            Assert.Equal("", payload.EventCore.TenantId);
            using var json = JsonDocument.Parse(message.PayloadJson);
            Assert.Equal(new[] { "eventCore", "revision", "storageProfileId" },
                json.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
            Assert.DoesNotContain(candidate.DisplayName, message.PayloadJson);
            Assert.DoesNotContain(replacement.DisplayName, message.PayloadJson);
            Assert.DoesNotContain("standard", message.PayloadJson);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutboxFailureRollsBackProfileAndDefaultDemotion(bool updating)
    {
        var candidate = Candidate();
        if (updating)
            await DbContext.StorageProfiles.InsertOneAsync(candidate);
        var defaultBefore = await DbContext.StorageProfiles
            .Find(profile => profile.Id == TestData.DefaultStorageProfileId).SingleAsync();
        var realStore = Services.GetRequiredService<StoreOutboxWrites>();
        StoreOutboxWrites failingStore = async (session, writes, ct) =>
        {
            await realStore(session, writes, ct);
            throw new InvalidOperationException("controlled outbox failure");
        };
        if (updating)
        {
            var persistence = new MongoPersistUpdatedStorageProfile(DbContext.StorageProfiles,
                Services.GetRequiredService<RunMongoOutcomeTransaction<PersistUpdatedStorageProfileOutcome>>(),
                failingStore);
            await Assert.ThrowsAsync<InvalidOperationException>(() => persistence.InvokeAsync(candidate,
                candidate with
                {
                    IsDefault = true,
                    Revision = 2,
                    UpdatedAtUtc = candidate.UpdatedAtUtc.AddSeconds(1)
                }));
            var persisted = await DbContext.StorageProfiles.Find(profile => profile.Id == candidate.Id).SingleAsync();
            Assert.False(persisted.IsDefault);
            Assert.Equal(1, persisted.Revision);
        }
        else
        {
            var persistence = new MongoPersistCreatedStorageProfile(DbContext.StorageProfiles,
                Services.GetRequiredService<RunMongoOutcomeTransaction<StorageProfile>>(),
                failingStore);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                persistence.InvokeAsync(candidate with
                {
                    IsDefault = true
                }));
            Assert.False(await DbContext.StorageProfiles.Find(profile => profile.Id == candidate.Id).AnyAsync());
        }

        var defaultAfter =
            await DbContext.StorageProfiles.Find(profile => profile.Id == defaultBefore.Id).SingleAsync();
        Assert.True(defaultAfter.IsDefault);
        Assert.Equal(defaultBefore.Revision, defaultAfter.Revision);
        Assert.Equal(defaultBefore.UpdatedAtUtc, defaultAfter.UpdatedAtUtc);
        Assert.Equal(0, await DbContext.CountOutboxMessagesAsync(EventType));
    }

    [Fact]
    public async Task StalePromotingUpdateDoesNotDemoteDefaultOrAppendAnEvent()
    {
        var candidate = Candidate();
        await DbContext.StorageProfiles.InsertOneAsync(candidate);
        var update = Services.GetRequiredService<PersistUpdatedStorageProfile>();
        var replacement = candidate with
        {
            DisplayName = "Accepted",
            Revision = 2,
            UpdatedAtUtc = candidate.UpdatedAtUtc.AddSeconds(1)
        };
        Assert.IsType<StorageProfile>((await update(candidate, replacement)).Value);
        Assert.IsType<StorageProfileRevisionConflict>((await update(candidate,
                candidate with
                {
                    IsDefault = true,
                    Revision = 2,
                    UpdatedAtUtc = candidate.UpdatedAtUtc.AddSeconds(2)
                }))
            .Value);
        var activeDefault = await DbContext.StorageProfiles
            .Find(profile => profile.Id == TestData.DefaultStorageProfileId).SingleAsync();
        Assert.True(activeDefault.IsDefault);
        Assert.Equal(1, activeDefault.Revision);
        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync(EventType));
    }

    [Fact]
    public async Task ConsumerKeepsCatalogIdentityInAdminHintAndUsesIdFreeEligibilityFanout()
    {
        var changes = new List<RealtimeChange>();
        var eligibility = new List<string>();
        var consumer = new NotifyStorageProfileCatalogChangedConsumer((change, _) =>
            {
                changes.Add(change);
                return Task.CompletedTask;
            },
            (topic, _) =>
            {
                eligibility.Add(topic);
                return Task.CompletedTask;
            });
        await consumer.HandleAsync(new StorageProfileCatalogChangedEvent("profile", 7));
        var change = Assert.Single(changes);
        Assert.Equal("storage-profiles", change.Topic);
        Assert.Null(change.TenantId);
        Assert.Equal("profile", change.ResourceId);
        Assert.Equal(7, change.Revision);
        Assert.Equal("storage-eligibility", Assert.Single(eligibility));
    }

    private static StorageProfile Candidate() => TestData.SeedStorageProfile(
            ObjectId.GenerateNewId().ToString(),
            isDefault: false) with
    {
        DisplayName = "Private storage catalog name"
    };
}
