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
    public async Task GetStorageProfiles_ReturnsProfiles_DefaultFirstThenDisplayName()
    {
        await DbContext.StorageProfiles.DeleteManyAsync(Builders<StorageProfile>.Filter.Empty);
        var archiveId = ObjectId.GenerateNewId().ToString();
        var defaultId = ObjectId.GenerateNewId().ToString();
        var bulkId = ObjectId.GenerateNewId().ToString();

        await DbContext.StorageProfiles.InsertManyAsync(
        [
            CreateProfile(archiveId, "Archive HDD", "bulk", ImmutableHashSet.Create(ResourceType.Postgres), isEnabled: false),
            CreateProfile(defaultId, "Default SSD", "standard", ImmutableHashSet.Create(ResourceType.Redis), isDefault: true),
            CreateProfile(bulkId, "Bulk SSD", "fast", ImmutableHashSet.Create(ResourceType.Redis))
        ]);

        var result = await Mediator.Send(new GetStorageProfilesRequest());

        Assert.True(result.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.NotNull(response);
        Assert.Equal([defaultId, archiveId, bulkId], response.Select(profile => profile.Id).ToArray());
        Assert.Contains(response, profile => profile.Id == archiveId && !profile.IsEnabled);
    }

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
    public async Task Update_ReplacesProfileAndUnsetsPreviousDefault()
    {
        await DbContext.StorageProfiles.DeleteManyAsync(Builders<StorageProfile>.Filter.Empty);
        var previousDefaultId = ObjectId.GenerateNewId().ToString();
        var targetId = ObjectId.GenerateNewId().ToString();
        await DbContext.StorageProfiles.InsertManyAsync(
        [
            CreateProfile(previousDefaultId, "Previous Default", "standard", ImmutableHashSet.Create(ResourceType.Redis), isDefault: true),
            CreateProfile(targetId, "Fast Redis", "fast", ImmutableHashSet.Create(ResourceType.Redis))
        ]);

        var result = await Mediator.Send(new UpdateStorageProfileRequest(
            targetId,
            ValidBody(
                displayName: "Fast Redis Updated",
                storageClassNames: ["fast"],
                isDefault: true,
                allowedResourceTypes: ["Redis"],
                sizeLimits: new StorageProfileSizeLimitsDto(2, 50))));

        Assert.True(result.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.NotNull(response);
        Assert.Equal(targetId, response.Id);
        Assert.Equal("Fast Redis Updated", response.DisplayName);
        Assert.True(response.IsDefault);
        Assert.Equal(new StorageProfileSizeLimitsDto(2, 50), response.SizeLimits);

        var previousDefault = await DbContext.StorageProfiles
            .Find(profile => profile.Id == previousDefaultId)
            .FirstOrDefaultAsync();
        var updated = await DbContext.StorageProfiles
            .Find(profile => profile.Id == targetId)
            .FirstOrDefaultAsync();

        Assert.NotNull(previousDefault);
        Assert.False(previousDefault.IsDefault);
        Assert.NotNull(updated);
        Assert.True(updated.IsDefault);
        Assert.Equal(StoragePerformanceTier.Fast, updated.PerformanceTier);
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
    public async Task GetKubernetesStorageClasses_ReturnsDiscoveredClasses()
    {
        var result = await Mediator.Send(new GetKubernetesStorageClassesRequest());

        Assert.True(result.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.NotNull(response);
        Assert.Equal(["standard", "fast", "bulk"], response.Select(storageClass => storageClass.Name).ToArray());
        Assert.True(response.First().IsDefault);
    }

    [Fact]
    public async Task GetKubernetesPersistentVolumes_ReturnsDiscoveredVolumes()
    {
        var result = await Mediator.Send(new GetKubernetesPersistentVolumesRequest());

        Assert.True(result.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.NotNull(response);
        var volume = Assert.Single(response);
        Assert.Equal("pv-standard-1", volume.Name);
        Assert.Equal("standard", volume.StorageClassName);
        Assert.Equal("20Gi", volume.Capacity);
    }

    [Fact]
    public async Task GetKubernetesPersistentVolumeClaims_ReturnsDiscoveredClaimsWithOwner()
    {
        var result = await Mediator.Send(new GetKubernetesPersistentVolumeClaimsRequest());

        Assert.True(result.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.NotNull(response);
        var claim = Assert.Single(response);
        Assert.Equal("redis-data-redis-main-0", claim.Name);
        Assert.Equal("test-alpha", claim.Namespace);
        Assert.Equal("standard", claim.StorageClassName);
        Assert.Equal(TestData.TenantId, claim.Owner.TenantId);
        Assert.Equal("Redis", claim.Owner.ResourceType);
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
        string displayName = "Fast SSD",
        IReadOnlyCollection<string>? storageClassNames = null,
        IReadOnlyCollection<string>? allowedResourceTypes = null,
        StorageProfileTenantAccessDto? tenantAccess = null,
        StorageProfileSizeLimitsDto? sizeLimits = null,
        bool isDefault = false) =>
        new(
            DisplayName: displayName,
            Description: "Fast tenant storage",
            MediaType: "SSD",
            PerformanceTier: "Fast",
            IsEnabled: true,
            IsDefault: isDefault,
            StorageClassNames: storageClassNames ?? ["fast"],
            AllowedResourceTypes: allowedResourceTypes ?? ["Redis", "Postgres"],
            TenantAccess: tenantAccess ?? new StorageProfileTenantAccessDto("AllTenants", []),
            SizeLimits: sizeLimits ?? new StorageProfileSizeLimitsDto(1, 100));

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
