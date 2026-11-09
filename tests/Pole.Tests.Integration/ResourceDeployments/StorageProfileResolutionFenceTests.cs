using MongoDB.Driver;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;
using Pole.Workloads;

namespace Pole.Tests.Integration.ResourceDeployments;

[Collection(IntegrationTestCollection.Global2)]
public sealed class StorageProfileResolutionFenceTests(
    MongoReplicaSetFixture fixture) : MongoIntegrationTestBase(fixture)
{
    private const string ProfileId = "507f1f77bcf86cd799439051";
    private const string TargetId = "507f1f77bcf86cd799439052";

    public override Task InitializeAsync() => base.InitializeAsync();

    public override Task DisposeAsync() => base.DisposeAsync();

    [Fact]
    public async Task Fence_WritesAgainstTheExactProfileRevisionAndTargetMapping()
    {
        var profile = StorageProfileFactory.Create(
            technicalName: "storage-" + MongoDB.Bson.ObjectId.GenerateNewId(),
            "fast",
            "Fast storage",
            StorageMediaType.SSD,
            StoragePerformanceTier.Fast,
            isEnabled: true,
            isDefault: false,
            [StorageProfileTargetMapping.Create(TargetId, "fast")],
            [ResourceTypeId.Create("third-party/widget")],
            StorageProfileTenantAccess.Create(
                StorageProfileTenantAccessMode.AllTenants,
                []),
            StorageProfileSizeLimits.Create(1, 100),
            new StorageProfileFactory.CreateOptions(
                ProfileId,
                DateTime.UtcNow));
        await Fixture.DbContext.StorageProfiles.InsertOneAsync(profile);
        var result = await FenceAsync(new StorageProfileResolutionFenceRequest(
            ProfileId,
            profile.Revision,
            TargetId,
            "fast"));

        Assert.IsType<StorageProfileResolutionFenced>(result.Value);
        var saved = await Fixture.DbContext.StorageProfiles
            .Find(item => item.Id == ProfileId)
            .SingleAsync();
        Assert.Equal(1, saved.ResolutionFence);
        Assert.Equal(profile.Revision, saved.Revision);
    }

    [Fact]
    public async Task Fence_RejectsAStaleResolution()
    {
        var profile = StorageProfileFactory.Create(
            technicalName: "storage-" + MongoDB.Bson.ObjectId.GenerateNewId(),
            "fast",
            "Fast storage",
            StorageMediaType.SSD,
            StoragePerformanceTier.Fast,
            isEnabled: true,
            isDefault: false,
            [StorageProfileTargetMapping.Create(TargetId, "fast")],
            [ResourceTypeId.Create("third-party/widget")],
            StorageProfileTenantAccess.Create(
                StorageProfileTenantAccessMode.AllTenants,
                []),
            StorageProfileSizeLimits.Create(1, 100),
            new StorageProfileFactory.CreateOptions(
                ProfileId,
                DateTime.UtcNow));
        await Fixture.DbContext.StorageProfiles.InsertOneAsync(profile);
        var result = await FenceAsync(new StorageProfileResolutionFenceRequest(
            ProfileId,
            profile.Revision + 1,
            TargetId,
            "fast"));

        Assert.IsType<StorageProfileResolutionFenceRejected>(result.Value);
    }

    [Fact]
    public async Task Fence_ThrowsForStructuralResolutionInput()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => FenceAsync(
            new StorageProfileResolutionFenceRequest(
                ProfileId,
                1,
                "not-an-object-id",
                "fast")));
    }

    private async Task<FenceStorageProfileResolutionOutcome> FenceAsync(
        StorageProfileResolutionFenceRequest request)
    {
        using var provider = TestServiceProviderFactory.Create(Fixture);
        using var scope = provider.CreateScope();
        var fence = scope.ServiceProvider.GetRequiredService<FenceStorageProfileResolution>();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        try
        {
            var result = await fence(request, session);
            if (result is StorageProfileResolutionFenced)
                await session.CommitTransactionAsync();
            else
                await session.AbortTransactionAsync();
            return result;
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }
    }
}
