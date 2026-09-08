using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Application.Endpoints;
using Pole.Features.StorageProfiles.Application.Endpoints.KubernetesInventory;
using Pole.Features.StorageProfiles.Application.Migrations;
using Pole.Features.StorageProfiles.Application.Persistence;
using Pole.Features.StorageProfiles.Application.Persistence.Mutations;
using Pole.Features.StorageProfiles.Application.Persistence.Reads;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;
using Pole.Workloads;

namespace Pole.Tests.Integration.StorageProfiles;

public sealed class StorageProfileIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private const string SecondDeploymentTargetId = "507f1f77bcf86cd799439097";

    [Fact]
    public async Task GetStorageProfiles_ReturnsProfiles_DefaultFirstThenDisplayName()
    {
        await DbContext.StorageProfiles.DeleteManyAsync(
            Builders<StorageProfile>.Filter.Empty);
        var archiveId = ObjectId.GenerateNewId().ToString();
        var defaultId = ObjectId.GenerateNewId().ToString();
        var bulkId = ObjectId.GenerateNewId().ToString();
        await DbContext.StorageProfiles.InsertManyAsync(
        [
            CreateProfile(
                archiveId,
                "Archive HDD",
                "bulk",
                [ResourceTypeId.Create("pole/postgresql")],
                isEnabled: false),
            CreateProfile(
                defaultId,
                "Default SSD",
                "standard",
                [ResourceTypeId.Create("third-party/widget")],
                isDefault: true),
            CreateProfile(
                bulkId,
                "Bulk SSD",
                "fast",
                [ResourceTypeId.Create("third-party/widget")])
        ]);

        var response = (await Mediator.Send(
            new GetStorageProfilesRequest())).AssertSuccess().AssertSuccess();

        Assert.Equal(
            [defaultId, archiveId, bulkId],
            response.Select(profile => profile.Id).ToArray());
        Assert.All(response, profile => Assert.Equal(1, profile.Revision));
        Assert.Contains(
            response,
            profile =>
                profile.Id == archiveId &&
                !profile.IsEnabled &&
                Assert.Single(profile.TargetMappings).DeploymentTargetId ==
                TestData.DefaultDeploymentTargetId);
    }

    [Fact]
    public async Task Create_ReturnsTypedError_ForUnknownTargetStorageClass()
    {
        var result = await Mediator.Send(new CreateStorageProfileRequest(
            ValidCreateBody(targetMappings:
            [
                new StorageProfileTargetMappingRequestBody(
                    TestData.DefaultDeploymentTargetId,
                    "missing-class")
            ])));

        var error = result.AssertSuccess().AssertFailure();
        var validation = Assert.IsType<StorageProfileMutationValidationError>(error.Value);
        var invalid = Assert.IsType<StorageProfileTargetMappingsInvalid>(validation.Value);
        Assert.Contains(
            "Body.TargetMappings[0].StorageClassName",
            invalid.Errors.Keys);
    }

    [Fact]
    public async Task Create_RequestValidationRejectsDuplicateTargetsAndBlankSelectedTenantIds()
    {
        var duplicateResult = await Mediator.Send(new CreateStorageProfileRequest(
            ValidCreateBody(targetMappings:
            [
                new StorageProfileTargetMappingRequestBody(
                    TestData.DefaultDeploymentTargetId,
                    "standard"),
                new StorageProfileTargetMappingRequestBody(
                    TestData.DefaultDeploymentTargetId,
                    "fast")
            ])));
        var duplicateValidation = duplicateResult.AssertValidationRejected();
        Assert.Contains(
            "Body.TargetMappings",
            duplicateValidation.Errors.Keys);

        var tenantResult = await Mediator.Send(new CreateStorageProfileRequest(
            ValidCreateBody(tenantAccess:
                new StorageProfileTenantAccessDto(
                    "SelectedTenants",
                    ["   "]))));
        var tenantValidation = tenantResult.AssertValidationRejected();
        Assert.Contains(
            "Body.TenantAccess.TenantIds[0]",
            tenantValidation.Errors.Keys);
    }

    [Fact]
    public async Task Create_PersistsTargetMappingsAndInitialRevision()
    {
        var response = (await Mediator.Send(new CreateStorageProfileRequest(
            ValidCreateBody()))).AssertSuccess().AssertSuccess();

        Assert.Equal(1, response.Revision);
        var mapping = Assert.Single(response.TargetMappings);
        Assert.Equal(TestData.DefaultDeploymentTargetId, mapping.DeploymentTargetId);
        Assert.Equal("fast", mapping.StorageClassName);
        var persisted = await DbContext.StorageProfiles
            .Find(profile => profile.Id == response.Id)
            .SingleAsync();
        Assert.Equal(1, persisted.Revision);
        Assert.Equal(
            StorageProfileTargetMapping.Create(
                TestData.DefaultDeploymentTargetId,
                "fast"),
            Assert.Single(persisted.TargetMappings));
    }

    [Fact]
    public async Task Update_IsRevisionFenced_AndRevisionizesPreviousDefaultDemotion()
    {
        await DbContext.StorageProfiles.DeleteManyAsync(
            Builders<StorageProfile>.Filter.Empty);
        var previousDefaultId = ObjectId.GenerateNewId().ToString();
        var targetId = ObjectId.GenerateNewId().ToString();
        await DbContext.StorageProfiles.InsertManyAsync(
        [
            CreateProfile(
                previousDefaultId,
                "Previous Default",
                "standard",
                [ResourceTypeId.Create("third-party/widget")],
                isDefault: true),
            CreateProfile(
                targetId,
                "Fast Widget",
                "fast",
                [ResourceTypeId.Create("third-party/widget")])
        ]);

        var response = (await Mediator.Send(new UpdateStorageProfileRequest(
            targetId,
            ValidUpdateBody(
                revision: 1,
                displayName: "Fast Widget Updated",
                isDefault: true,
                allowedResourceTypes: ["third-party/widget"],
                sizeLimits: new StorageProfileSizeLimitsDto(2, 50)))))
            .AssertSuccess().AssertSuccess();

        Assert.Equal(targetId, response.Id);
        Assert.Equal("Fast Widget Updated", response.DisplayName);
        Assert.True(response.IsDefault);
        Assert.Equal(2, response.Revision);
        Assert.Equal(new StorageProfileSizeLimitsDto(2, 50), response.SizeLimits);

        var previousDefault = await DbContext.StorageProfiles
            .Find(profile => profile.Id == previousDefaultId)
            .SingleAsync();
        Assert.False(previousDefault.IsDefault);
        Assert.Equal(2, previousDefault.Revision);

        var stale = await Mediator.Send(new UpdateStorageProfileRequest(
            targetId,
            ValidUpdateBody(
                revision: 1,
                displayName: "Stale update")));
        var staleError = stale.AssertSuccess().AssertFailure();
        Assert.IsType<StorageProfileRevisionConflict>(staleError.Value);
    }

    [Fact]
    public async Task Update_NoOpPreservesRevision()
    {
        var existing = await DbContext.StorageProfiles
            .Find(profile => profile.Id == TestData.DefaultStorageProfileId)
            .SingleAsync();

        var response = (await Mediator.Send(new UpdateStorageProfileRequest(
            existing.Id,
            new UpdateStorageProfileRequestBody(
                existing.DisplayName,
                existing.Description,
                existing.MediaType.ToString(),
                existing.PerformanceTier.ToString(),
                existing.IsEnabled,
                existing.IsDefault,
                existing.TargetMappings
                    .Select(mapping => new StorageProfileTargetMappingRequestBody(
                        mapping.DeploymentTargetId,
                        mapping.StorageClassName))
                    .ToArray(),
                existing.AllowedResourceTypes
                    .Select(type => type.ToString())
                    .ToArray(),
                new StorageProfileTenantAccessDto(
                    existing.TenantAccess.Mode.ToString(),
                    existing.TenantAccess.TenantIds.ToArray()),
                new StorageProfileSizeLimitsDto(
                    existing.SizeLimits!.MinSizeGb,
                    existing.SizeLimits.MaxSizeGb),
                existing.Revision))))
            .AssertSuccess().AssertSuccess();

        Assert.Equal(existing.Revision, response.Revision);
        var persisted = await DbContext.StorageProfiles
            .Find(profile => profile.Id == existing.Id)
            .SingleAsync();
        Assert.Equal(existing.UpdatedAtUtc, persisted.UpdatedAtUtc);
    }

    [Fact]
    public async Task Update_DoesNotOverwriteAResolutionFenceWrittenAfterItsSnapshot()
    {
        var profile = CreateProfile(
            ObjectId.GenerateNewId().ToString(),
            "Fenced profile",
            "fast",
            [ResourceTypeId.Create("third-party/widget")]);
        await DbContext.StorageProfiles.InsertOneAsync(profile);
        var fence = Services.GetRequiredService<FenceStorageProfileResolution>();
        using (var session = await Fixture.Client.StartSessionAsync())
        {
            session.StartTransaction();
            var fenced = await fence(
                new StorageProfileResolutionFenceRequest(
                    profile.Id,
                    profile.Revision,
                    TestData.DefaultDeploymentTargetId,
                    "fast"),
                session);
            fenced.AssertSuccess();
            await session.CommitTransactionAsync();
        }

        var replacement = profile.Reconfigure(
            "Fenced profile updated",
            profile.Description,
            profile.MediaType,
            profile.PerformanceTier,
            profile.IsEnabled,
            profile.IsDefault,
            profile.TargetMappings,
            profile.AllowedResourceTypes.Select(ResourceTypeId.Create),
            profile.TenantAccess,
            profile.SizeLimits,
            profile.UpdatedAtUtc.AddSeconds(1));
        var update = Services.GetRequiredService<PersistUpdatedStorageProfile>();

        var result = await update(profile, replacement);

        Assert.IsType<StorageProfileRevisionConflict>(result.AssertFailure());
        var saved = await DbContext.StorageProfiles
            .Find(item => item.Id == profile.Id)
            .SingleAsync();
        Assert.Equal(profile.Revision, saved.Revision);
        Assert.Equal(1, saved.ResolutionFence);
        Assert.Equal(profile.DisplayName, saved.DisplayName);
    }

    [Fact]
    public async Task GetAvailable_UsesOnlyTheSelectedTargetMapping()
    {
        await DbContext.StorageProfiles.DeleteManyAsync(
            Builders<StorageProfile>.Filter.Empty);
        var bothTargetsId = ObjectId.GenerateNewId().ToString();
        var defaultOnlyId = ObjectId.GenerateNewId().ToString();
        await DbContext.StorageProfiles.InsertManyAsync(
        [
            CreateProfile(
                bothTargetsId,
                "Both targets",
                "standard",
                [ResourceTypeId.Create("third-party/widget")],
                additionalMappings:
                [
                    StorageProfileTargetMapping.Create(
                        SecondDeploymentTargetId,
                        "second-fast")
                ]),
            CreateProfile(
                defaultOnlyId,
                "Default only",
                "fast",
                [ResourceTypeId.Create("third-party/widget")])
        ]);
        var discovery = new TestStorageDiscoveryService(
            new Dictionary<string, IReadOnlyCollection<string>>
            {
                [TestData.DefaultDeploymentTargetId] = ["standard", "fast"],
                [SecondDeploymentTargetId] = ["second-fast"]
            });
        var services = new ServiceCollection();
        services.AddSingleton<IMongoDatabase>(Fixture.Database);
        services.AddSingleton(discovery);
        services.AddSingleton<DiscoverKubernetesStorageClassNames>(
            discovery.GetStorageClassNamesAsync);
        services.AddStorageProfilesFeature();
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var handler = new GetAvailableStorageProfilesHandler(
            scope.ServiceProvider.GetRequiredService<DiscoverKubernetesStorageClassNames>(),
            scope.ServiceProvider.GetRequiredService<LoadAvailableStorageProfiles>());

        var response = (await handler.HandleAsync(
            new GetAvailableStorageProfilesRequest(
                "third-party/widget",
                SecondDeploymentTargetId,
                TestData.TenantId))).AssertSuccess();

        Assert.Equal([bothTargetsId], response.Select(profile => profile.Id).ToArray());
        Assert.Equal(
            [SecondDeploymentTargetId],
            discovery.RequestedDeploymentTargetIds);
    }

    [Fact]
    public async Task InventoryEndpoints_RouteEveryQueryToTheRequestedTarget()
    {
        var discovery = Services.GetRequiredService<TestStorageDiscoveryService>();

        var classes = (await Mediator.Send(
            new GetKubernetesStorageClassesRequest(
                TestData.DefaultDeploymentTargetId))).AssertSuccess().AssertSuccess();
        var volumes = (await Mediator.Send(
            new GetKubernetesPersistentVolumesRequest(
                TestData.DefaultDeploymentTargetId))).AssertSuccess().AssertSuccess();
        var claims = (await Mediator.Send(
            new GetKubernetesPersistentVolumeClaimsRequest(
                TestData.DefaultDeploymentTargetId))).AssertSuccess().AssertSuccess();

        Assert.Equal(["bulk", "fast", "standard"], classes.Select(item => item.Name).ToArray());
        Assert.Equal("pv-standard-1", Assert.Single(volumes).Name);
        Assert.Equal(
            TestData.TenantId,
            Assert.Single(claims).Owner.TenantId);
        Assert.Equal(
            Enumerable.Repeat(TestData.DefaultDeploymentTargetId, 3),
            discovery.RequestedDeploymentTargetIds);
    }

    [Fact]
    public async Task InventoryRequestValidation_RejectsInvalidTargetId()
    {
        var result = await Mediator.Send(
            new GetKubernetesStorageClassesRequest("not-an-object-id"));

        var validation = result.AssertValidationRejected();
        Assert.Contains("DeploymentTargetId", validation.Errors.Keys);
    }

    [Fact]
    public async Task Resolve_ReturnsExactTargetMappingAndProfileRevision()
    {
        await DbContext.StorageProfiles.DeleteManyAsync(
            Builders<StorageProfile>.Filter.Empty);
        var profile = CreateProfile(
            ObjectId.GenerateNewId().ToString(),
            "Multi-target Widget",
            "standard",
            [ResourceTypeId.Create("third-party/widget")],
            additionalMappings:
            [
                StorageProfileTargetMapping.Create(
                    SecondDeploymentTargetId,
                    "second-fast")
            ]);
        await DbContext.StorageProfiles.InsertOneAsync(profile);
        var discovery = new TestStorageDiscoveryService(
            new Dictionary<string, IReadOnlyCollection<string>>
            {
                [TestData.DefaultDeploymentTargetId] = ["standard"],
                [SecondDeploymentTargetId] = ["second-fast"]
            });
        var services = new ServiceCollection();
        services.AddSingleton<IMongoDatabase>(Fixture.Database);
        services.AddSingleton(discovery);
        services.AddSingleton<DiscoverKubernetesStorageClassNames>(
            discovery.GetStorageClassNamesAsync);
        services.AddStorageProfilesFeature();
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var resolve = scope.ServiceProvider.GetRequiredService<ResolveStorageProfile>();

        var resolved = (await resolve(
            new StorageProfileResolutionRequest(
                profile.Id,
                SecondDeploymentTargetId,
                TestData.TenantId,
                ResourceTypeId.Create("third-party/widget"),
                SizeGb: 5))).AssertSuccess();

        Assert.Equal(profile.Id, resolved.ProfileId);
        Assert.Equal(profile.Revision, resolved.ProfileRevision);
        Assert.Equal(SecondDeploymentTargetId, resolved.DeploymentTargetId);
        Assert.Equal("second-fast", resolved.StorageClassName);
        Assert.Equal(
            [SecondDeploymentTargetId],
            discovery.RequestedDeploymentTargetIds);
    }

    [Fact]
    public async Task Resolve_RejectsMissingTargetMappingWithoutQueryingAnotherTarget()
    {
        var resolve = Services.GetRequiredService<ResolveStorageProfile>();
        var discovery = Services.GetRequiredService<TestStorageDiscoveryService>();

        var result = await resolve(
            new StorageProfileResolutionRequest(
                TestData.DefaultStorageProfileId,
                SecondDeploymentTargetId,
                TestData.TenantId,
                ResourceTypeId.Create("third-party/widget"),
                SizeGb: 5));

        Assert.IsType<StorageProfileDeploymentTargetNotConfigured>(
            result.AssertFailure().Value);
        Assert.Empty(discovery.RequestedDeploymentTargetIds);
    }

    [Fact]
    public async Task Resolve_ReturnsTypedError_ForCrossTenantProfile()
    {
        await DbContext.StorageProfiles.DeleteManyAsync(
            Builders<StorageProfile>.Filter.Empty);
        var profileId = ObjectId.GenerateNewId().ToString();
        await DbContext.StorageProfiles.InsertOneAsync(CreateProfile(
            profileId,
            "Other Tenant Redis",
            "standard",
            [ResourceTypeId.Create("third-party/widget")],
            tenantAccess: StorageProfileTenantAccess.Create(
                StorageProfileTenantAccessMode.SelectedTenants,
                ["507f1f77bcf86cd799439205"])));
        var resolve = Services.GetRequiredService<ResolveStorageProfile>();

        var result = await resolve(new StorageProfileResolutionRequest(
            profileId,
            TestData.DefaultDeploymentTargetId,
            TestData.TenantId,
            ResourceTypeId.Create("third-party/widget"),
            SizeGb: 5));

        Assert.IsType<StorageProfileTenantAccessDenied>(result.AssertFailure().Value);
    }

    [Fact]
    public async Task Resolve_ThrowsForStructuralContractInput()
    {
        var resolve = Services.GetRequiredService<ResolveStorageProfile>();

        await Assert.ThrowsAsync<ArgumentException>(() => resolve(
            new StorageProfileResolutionRequest(
                "not-an-object-id",
                TestData.DefaultDeploymentTargetId,
                TestData.TenantId,
                ResourceTypeId.Create("third-party/widget"),
                SizeGb: 5)));
    }

    [Fact]
    public async Task Migration_CreatesTargetAvailabilityAndSingleDefaultIndexes()
    {
        await new StorageProfileMigration().Up(Fixture.Database);
        var indexes = await Fixture.Database
            .GetCollection<BsonDocument>("StorageProfiles")
            .Indexes
            .List()
            .ToListAsync();

        Assert.Contains(
            indexes,
            index => index["name"] == "UX_StorageProfiles_Default");
        Assert.Contains(
            indexes,
            index => index["name"] == "IX_StorageProfiles_Target_Availability");

        await Assert.ThrowsAsync<MongoWriteException>(() =>
            DbContext.StorageProfiles.InsertOneAsync(CreateProfile(
                ObjectId.GenerateNewId().ToString(),
                "Second default",
                "fast",
                [ResourceTypeId.Create("third-party/widget")],
                isDefault: true)));
    }

    private static CreateStorageProfileRequestBody ValidCreateBody(
        string displayName = "Fast SSD",
        IReadOnlyCollection<StorageProfileTargetMappingRequestBody>? targetMappings = null,
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
            TargetMappings: targetMappings ??
            [
                new StorageProfileTargetMappingRequestBody(
                    TestData.DefaultDeploymentTargetId,
                    "fast")
            ],
            AllowedResourceTypes: allowedResourceTypes ?? ["third-party/widget", "pole/postgresql"],
            TenantAccess: tenantAccess ??
                new StorageProfileTenantAccessDto("AllTenants", []),
            SizeLimits: sizeLimits ?? new StorageProfileSizeLimitsDto(1, 100));

    private static UpdateStorageProfileRequestBody ValidUpdateBody(
        long revision,
        string displayName = "Fast SSD",
        IReadOnlyCollection<StorageProfileTargetMappingRequestBody>? targetMappings = null,
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
            TargetMappings: targetMappings ??
            [
                new StorageProfileTargetMappingRequestBody(
                    TestData.DefaultDeploymentTargetId,
                    "fast")
            ],
            AllowedResourceTypes: allowedResourceTypes ?? ["third-party/widget", "pole/postgresql"],
            TenantAccess: tenantAccess ??
                new StorageProfileTenantAccessDto("AllTenants", []),
            SizeLimits: sizeLimits ?? new StorageProfileSizeLimitsDto(1, 100),
            Revision: revision);

    private static StorageProfile CreateProfile(
        string id,
        string displayName,
        string storageClassName,
        IEnumerable<ResourceTypeId> allowedResourceTypes,
        StorageProfileTenantAccess? tenantAccess = null,
        bool isEnabled = true,
        bool isDefault = false,
        IEnumerable<StorageProfileTargetMapping>? additionalMappings = null)
    {
        var mappings = new[]
            {
                StorageProfileTargetMapping.Create(
                    TestData.DefaultDeploymentTargetId,
                    storageClassName)
            }
            .Concat(additionalMappings ?? []);
        return StorageProfileFactory.Create(
            displayName,
            description: displayName,
            StorageMediaType.SSD,
            StoragePerformanceTier.Standard,
            isEnabled,
            isDefault,
            mappings,
            allowedResourceTypes,
            tenantAccess ?? StorageProfileTenantAccess.Create(
                StorageProfileTenantAccessMode.AllTenants,
                []),
            StorageProfileSizeLimits.Create(1, 100),
            StorageProfileFactory.DefaultOptions with
            {
                Id = id
            });
    }
}
