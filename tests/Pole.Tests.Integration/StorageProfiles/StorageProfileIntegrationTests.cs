using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Shared.Models;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Endpoints;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.StorageProfiles;

public sealed class StorageProfileIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Create_ReturnsValidationFailure_ForUnknownStorageClass()
    {
        var result = await Mediator.Send(new CreateStorageProfileRequest(
            ValidBody(storageClassNames: ["missing-class"])));

        Assert.False(result.TryGetValue(out _, out var error));
        var validation = Assert.IsType<ValidationFailure>(error);
        Assert.Contains("Body.StorageClassNames", validation.ValidationErrors.Keys);
    }

    [Fact]
    public async Task Create_ReturnsValidationFailure_ForBlankSelectedTenantIds()
    {
        var result = await Mediator.Send(new CreateStorageProfileRequest(
            ValidBody(tenantAccess: new StorageProfileTenantAccessDto("SelectedTenants", ["   "]))));

        Assert.False(result.TryGetValue(out _, out var error));
        var validation = Assert.IsType<ValidationFailure>(error);
        Assert.Contains("Body.TenantAccess.TenantIds", validation.ValidationErrors.Keys);
    }

    [Fact]
    public async Task GetAvailable_FiltersByTenantResourceTypeEnabledStateAndKnownStorageClass()
    {
        await DbContext.StorageProfiles.DeleteManyAsync(Builders<StorageProfile>.Filter.Empty);
        var redisDefaultId = ObjectId.GenerateNewId().ToString();
        var redisSelectedId = ObjectId.GenerateNewId().ToString();

        await DbContext.StorageProfiles.InsertManyAsync(
        [
            CreateProfile(redisDefaultId, "Default Redis", "standard", ImmutableHashSet.Create(ResourceType.Redis), isDefault: true),
            CreateProfile(
                redisSelectedId,
                "Tenant Fast Redis",
                "fast",
                ImmutableHashSet.Create(ResourceType.Redis),
                tenantAccess: SelectedTenantAccess(TestData.TenantId)),
            CreateProfile("507f1f77bcf86cd799439201", "Web Only", "bulk", ImmutableHashSet.Create(ResourceType.WebService)),
            CreateProfile(
                "507f1f77bcf86cd799439202",
                "Other Tenant",
                "fast",
                ImmutableHashSet.Create(ResourceType.Redis),
                tenantAccess: SelectedTenantAccess("507f1f77bcf86cd799439202")),
            CreateProfile("507f1f77bcf86cd799439203", "Stale Class", "retired", ImmutableHashSet.Create(ResourceType.Redis)),
            CreateProfile("507f1f77bcf86cd799439204", "Disabled", "standard", ImmutableHashSet.Create(ResourceType.Redis), isEnabled: false)
        ]);

        var result = await Mediator.Send(new GetAvailableStorageProfilesRequest("Redis", TestData.TenantId));

        Assert.True(result.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.NotNull(response);
        var ids = response.Select(profile => profile.Id).ToArray();
        Assert.Equal([redisDefaultId, redisSelectedId], ids);
    }

    [Fact]
    public async Task Resolve_ReturnsValidationFailure_ForCrossTenantProfile()
    {
        await DbContext.StorageProfiles.DeleteManyAsync(Builders<StorageProfile>.Filter.Empty);
        var profileId = ObjectId.GenerateNewId().ToString();
        await DbContext.StorageProfiles.InsertOneAsync(CreateProfile(
            profileId,
            "Other Tenant Redis",
            "standard",
            ImmutableHashSet.Create(ResourceType.Redis),
            tenantAccess: SelectedTenantAccess("507f1f77bcf86cd799439205")));
        var resolver = Services.GetRequiredService<IStorageProfileResolver>();

        var result = await resolver.ResolveAsync(new StorageProfileResolutionRequest(
            profileId,
            TestData.TenantId,
            ResourceType.Redis,
            SizeGb: 5));

        Assert.False(result.TryGetValue(out _, out var error));
        var validation = Assert.IsType<ValidationFailure>(error);
        Assert.Contains("StorageProfileId", validation.ValidationErrors.Keys);
    }

    private static UpsertStorageProfileRequestBody ValidBody(
        IReadOnlyCollection<string>? storageClassNames = null,
        StorageProfileTenantAccessDto? tenantAccess = null) =>
        new(
            DisplayName: "Fast SSD",
            Description: "Fast tenant storage",
            MediaType: "SSD",
            PerformanceTier: "Fast",
            IsEnabled: true,
            IsDefault: false,
            StorageClassNames: storageClassNames ?? ["fast"],
            AllowedResourceTypes: ["Redis", "Postgres"],
            TenantAccess: tenantAccess ?? new StorageProfileTenantAccessDto("AllTenants", []),
            SizeLimits: new StorageProfileSizeLimitsDto(1, 100));

    private static StorageProfile CreateProfile(
        string id,
        string displayName,
        string storageClassName,
        ImmutableHashSet<ResourceType> allowedResourceTypes,
        StorageProfileTenantAccess? tenantAccess = null,
        bool isEnabled = true,
        bool isDefault = false) =>
        StorageProfileFactory.Create(
            displayName,
            description: displayName,
            StorageMediaType.SSD,
            StoragePerformanceTier.Standard,
            isEnabled,
            isDefault,
            ImmutableList.Create(storageClassName),
            allowedResourceTypes,
            tenantAccess ?? new StorageProfileTenantAccess(
                StorageProfileTenantAccessMode.AllTenants,
                ImmutableHashSet<string>.Empty),
            new StorageProfileSizeLimits(1, 100),
            StorageProfileFactory.DefaultCreateOptions with
            {
                Id = id
            });

    private static StorageProfileTenantAccess SelectedTenantAccess(string tenantId) =>
        new(
            StorageProfileTenantAccessMode.SelectedTenants,
            ImmutableHashSet.Create(tenantId));
}
