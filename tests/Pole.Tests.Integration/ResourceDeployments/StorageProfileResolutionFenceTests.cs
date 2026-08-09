using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Shared.Models;
using Pole.Features.StorageProfiles.Application.Common;
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
        var fence = new StorageProfileResolutionFence(fixture.DbContext);

        var result = await fixture.DbContext.RunInTransactionAsync(
            (session, cancellationToken) => fence.FenceAsync(
                new StorageProfileResolutionFenceRequest(
                    ProfileId,
                    profile.Revision,
                    TargetId,
                    "fast"),
                session,
                cancellationToken));

        Assert.IsAssignableFrom<ISuccess>(result);
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
        var fence = new StorageProfileResolutionFence(fixture.DbContext);

        var result = await fixture.DbContext.RunInTransactionAsync(
            (session, cancellationToken) => fence.FenceAsync(
                new StorageProfileResolutionFenceRequest(
                    ProfileId,
                    profile.Revision + 1,
                    TargetId,
                    "fast"),
                session,
                cancellationToken));

        Assert.IsType<ConflictFailure>(result);
    }
}
