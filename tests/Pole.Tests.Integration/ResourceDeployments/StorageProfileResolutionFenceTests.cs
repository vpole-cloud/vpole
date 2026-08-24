using MongoDB.Driver;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Shared.Models;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

[Collection(IntegrationTestCollection.Name)]
public sealed class StorageProfileResolutionFenceTests(
    MongoReplicaSetFixture fixture)
    : IAsyncLifetime
{
    private const string ProfileId = "507f1f77bcf86cd799439051";
    private const string TargetId = "507f1f77bcf86cd799439052";

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Fence_WritesAgainstTheExactProfileRevisionAndTargetMapping()
    {
        var profile = StorageProfileFactory.Create(
            "fast",
            "Fast storage",
            StorageMediaType.SSD,
            StoragePerformanceTier.Fast,
            isEnabled: true,
            isDefault: false,
            [StorageProfileTargetMapping.Create(TargetId, "fast")],
            [ResourceType.Redis],
            StorageProfileTenantAccess.Create(
                StorageProfileTenantAccessMode.AllTenants,
                []),
            StorageProfileSizeLimits.Create(1, 100),
            new StorageProfileFactory.CreateOptions(
                ProfileId,
                DateTime.UtcNow));
        await fixture.DbContext.StorageProfiles.InsertOneAsync(profile);
        var result = await FenceAsync(new StorageProfileResolutionFenceRequest(
            ProfileId,
            profile.Revision,
            TargetId,
            "fast"));

        result.AssertSuccess();
        var saved = await fixture.DbContext.StorageProfiles
            .Find(item => item.Id == ProfileId)
            .SingleAsync();
        Assert.Equal(1, saved.ResolutionFence);
        Assert.Equal(profile.Revision, saved.Revision);
    }

    [Fact]
    public async Task Fence_RejectsAStaleResolution()
    {
        var profile = StorageProfileFactory.Create(
            "fast",
            "Fast storage",
            StorageMediaType.SSD,
            StoragePerformanceTier.Fast,
            isEnabled: true,
            isDefault: false,
            [StorageProfileTargetMapping.Create(TargetId, "fast")],
            [ResourceType.Redis],
            StorageProfileTenantAccess.Create(
                StorageProfileTenantAccessMode.AllTenants,
                []),
            StorageProfileSizeLimits.Create(1, 100),
            new StorageProfileFactory.CreateOptions(
                ProfileId,
                DateTime.UtcNow));
        await fixture.DbContext.StorageProfiles.InsertOneAsync(profile);
        var result = await FenceAsync(new StorageProfileResolutionFenceRequest(
            ProfileId,
            profile.Revision + 1,
            TargetId,
            "fast"));

        Assert.IsType<StorageProfileResolutionFenceRejected>(result.AssertFailure());
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

    private async Task<Result<Unit, StorageProfileResolutionFenceRejected>> FenceAsync(
        StorageProfileResolutionFenceRequest request)
    {
        using var provider = TestServiceProviderFactory.Create(fixture);
        using var scope = provider.CreateScope();
        var fence = scope.ServiceProvider.GetRequiredService<FenceStorageProfileResolution>();
        using var session = await fixture.Client.StartSessionAsync();
        session.StartTransaction();
        try
        {
            var result = await fence(request, session);
            if (result is Ok<Unit>)
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
