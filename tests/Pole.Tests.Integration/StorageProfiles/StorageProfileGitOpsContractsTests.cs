using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Migrations;
using Pole.Features.StorageProfiles.Application.Catalog;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.StorageProfiles;

public sealed class StorageProfileGitOpsContractsTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private IMongoCollection<StorageProfileRevision> History =>
        Fixture.Database.GetCollection<StorageProfileRevision>("StorageProfileRevisions");

    [Theory]
    [InlineData("")]
    [InlineData("Uppercase")]
    [InlineData("1disk")]
    [InlineData("invalid_name")]
    public void FactoryRejectsInvalidTechnicalNames(string name)
    {
        var profile = Candidate();
        Assert.Throws<ArgumentException>(() => StorageProfileFactory.Create(name, profile.DisplayName,
            profile.Description, profile.MediaType, profile.PerformanceTier, profile.IsEnabled, profile.IsDefault,
            profile.TargetMappings, [ResourceTypeId.Create("third-party/widget")], profile.TenantAccess,
            profile.SizeLimits, StorageProfileFactory.DefaultOptions));
    }

    [Fact]
    public async Task ConcurrentCreateUsesGlobalNameIndexAndRollsBackLoserHistoryAndOutbox()
    {
        await new StorageProfileMigration().Up(Fixture.Database);
        var first = Candidate() with { TechnicalName = "shared-storage" };
        var second = Candidate() with { TechnicalName = first.TechnicalName };
        var create = Services.GetRequiredService<PersistCreatedStorageProfile>();
        var results = await Task.WhenAll(create(first), create(second));
        Assert.Single(results, result => result.Value is StorageProfile);
        Assert.Single(results, result => result.Value is StorageProfileTechnicalNameConflict);
        Assert.Equal(1, await History.CountDocumentsAsync(Builders<StorageProfileRevision>.Filter.Empty));
        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync(
            "pole.storage-profiles/v1/storage-profile-catalog-changed"));
    }

    [Fact]
    public async Task HistoryRoundTripsAndNoOpDoesNotAppendHistoryOrOutbox()
    {
        var profile = Candidate();
        var create = Services.GetRequiredService<PersistCreatedStorageProfile>();
        Assert.IsType<StorageProfile>((await create(profile)).Value);
        var update = Services.GetRequiredService<PersistUpdatedStorageProfile>();
        Assert.IsType<StorageProfile>((await update(profile, profile)).Value);
        var revision = await History.Find(Builders<StorageProfileRevision>.Filter.Empty).SingleAsync();
        Assert.Equal(profile.Id, revision.StorageProfileId);
        Assert.Equal(1, revision.Revision);
        Assert.Equal(profile.ToBson(), revision.Snapshot.ToBson());
        var roundTrip = BsonSerializer.Deserialize<StorageProfileRevision>(revision.ToBson());
        Assert.Equal(revision.ToBson(), roundTrip.ToBson());
        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync(
            "pole.storage-profiles/v1/storage-profile-catalog-changed"));
        await Assert.ThrowsAsync<ArgumentException>(() => update(profile,
            profile with { TechnicalName = "renamed", Revision = 2 }));
    }

    [Fact]
    public async Task HistoricalResolutionKeepsClassButUsesCurrentSizeAndTenantPolicy()
    {
        var profile = await CreateAsync();
        var replacement = profile with
        {
            Revision = 2,
            TargetMappings = [StorageProfileTargetMapping.Create(TestData.DefaultDeploymentTargetId, "fast")],
            SizeLimits = StorageProfileSizeLimits.Create(1, 20),
            UpdatedAtUtc = profile.UpdatedAtUtc.AddSeconds(1)
        };
        Assert.IsType<StorageProfile>((await Services.GetRequiredService<PersistUpdatedStorageProfile>()(
            profile, replacement)).Value);
        var resolve = Services.GetRequiredService<ResolveNamedStorageProfile>();
        var request = Named(profile, 10);
        var historical = Assert.IsType<ResolvedStorageProfile>((await resolve(request, default)).Value);
        Assert.Equal("standard", historical.StorageClassName);
        Assert.Equal(1, historical.ProfileRevision);
        var denied = Assert.IsType<StorageProfileResolutionError>(
            (await resolve(request with { SizeGb = 30 }, default)).Value);
        Assert.IsType<StorageProfileSizeNotAllowed>(denied.Value);
        var restricted = replacement with
        {
            Revision = 3,
            TenantAccess = StorageProfileTenantAccess.Create(StorageProfileTenantAccessMode.SelectedTenants,
                [ObjectId.GenerateNewId().ToString()])
        };
        await Services.GetRequiredService<PersistUpdatedStorageProfile>()(replacement, restricted);
        Assert.IsType<NamedStorageProfileUnavailable>((await resolve(request, default)).Value);
    }

    [Fact]
    public async Task PlanningFenceUsesCurrentPolicyAndRejectsStaleSnapshots()
    {
        var profile = await CreateAsync();
        var read = Services.GetRequiredService<ResolveStorageProfilePlanningSnapshot>();
        var fence = Services.GetRequiredService<FenceStorageProfilePlanningSnapshot>();
        var request = new StorageProfilePlanningRequest(TestData.TenantId,
            ResourceTypeId.Create("third-party/widget"), profile.Id, TestData.DefaultDeploymentTargetId, 10, 1);
        StorageProfilePlanningSnapshot snapshot;
        using (var session = await Fixture.Database.Client.StartSessionAsync())
        {
            session.StartTransaction();
            snapshot = Assert.IsType<StorageProfilePlanningSnapshot>((await read(request, session, default)).Value);
            Assert.IsType<StorageProfilePlanningFenced>((await fence(snapshot, session, default)).Value);
            await session.CommitTransactionAsync();
        }

        var fenced = await DbContext.StorageProfiles.Find(item => item.Id == profile.Id).SingleAsync();
        Assert.Equal(1, fenced.ResolutionFence);
        Assert.Equal(profile.Revision, fenced.Revision);
        var replacement = fenced with { Revision = 2, Description = "New policy revision" };
        await Services.GetRequiredService<PersistUpdatedStorageProfile>()(fenced, replacement);
        using var next = await Fixture.Database.Client.StartSessionAsync();
        next.StartTransaction();
        Assert.IsType<StorageProfilePlanningFenceRejected>((await fence(snapshot, next, default)).Value);
        await next.AbortTransactionAsync();
    }

    [Fact]
    public async Task CreationFenceAllowsExplicitPinnedHistoryButPreservesOrdinaryRevisionConflicts()
    {
        var profile = await CreateAsync();
        var replacement = profile with
        {
            Revision = 2,
            TargetMappings = [StorageProfileTargetMapping.Create(TestData.DefaultDeploymentTargetId, "fast")]
        };
        await Services.GetRequiredService<PersistUpdatedStorageProfile>()(profile, replacement);
        var fence = Services.GetRequiredService<FenceStorageProfileResolution>();
        var input = new StorageProfileResolutionFenceRequest(profile.Id, 1,
            TestData.DefaultDeploymentTargetId, "standard");
        using var session = await Fixture.Database.Client.StartSessionAsync();
        session.StartTransaction();
        Assert.IsType<StorageProfileResolutionFenceRejected>((await fence(input, session)).Value);
        Assert.IsType<StorageProfileResolutionFenced>((await fence(input with { PolicyRevision = 2 }, session)).Value);
        Assert.IsType<StorageProfileResolutionFenceRejected>(
            (await fence(input with { PolicyRevision = 1 }, session)).Value);
        Assert.IsType<StorageProfileResolutionFenceRejected>(
            (await fence(input with { PolicyRevision = 2, StorageClassName = "fast" }, session)).Value);
        await session.CommitTransactionAsync();
    }

    [Fact]
    public async Task DefaultDemotionRetainsBothRevisionsInSameTransaction()
    {
        var original = await DbContext.StorageProfiles.Find(item => item.Id == TestData.DefaultStorageProfileId)
            .SingleAsync();
        await CreateAsync(isDefault: true);
        var history = await History.Find(item => item.StorageProfileId == original.Id).SortBy(item => item.Revision)
            .ToListAsync();
        Assert.Equal(new long[] { 1, 2 }, history.Select(item => item.Revision));
        Assert.True(history[0].Snapshot.IsDefault);
        Assert.False(history[1].Snapshot.IsDefault);
    }

    private async Task<StorageProfile> CreateAsync(bool isDefault = false)
    {
        var profile = Candidate() with { IsDefault = isDefault };
        return Assert.IsType<StorageProfile>((await Services.GetRequiredService<PersistCreatedStorageProfile>()(
            profile)).Value);
    }

    private static NamedStorageProfileRequest Named(StorageProfile profile, int size) =>
        new(TestData.TenantId, ResourceTypeId.Create("third-party/widget"), profile.TechnicalName,
            TestData.DefaultDeploymentTargetId, size, 1);

    private static StorageProfile Candidate() =>
        TestData.SeedStorageProfile(ObjectId.GenerateNewId().ToString(), isDefault: false);
}
