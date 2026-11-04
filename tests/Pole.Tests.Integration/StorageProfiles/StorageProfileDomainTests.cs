using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain;
using Xunit;
using Pole.Workloads;

namespace Pole.Tests.Integration.StorageProfiles;

public sealed class StorageProfileDomainTests
{
    private const string FirstTargetId = "507f1f77bcf86cd799439096";
    private const string SecondTargetId = "507f1f77bcf86cd799439097";
    private static readonly DateTime CreatedAtUtc =
        new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Factory_NormalizesMappings_StartsRevision_AndRoundTripsBson()
    {
        var profile = CreateProfile(
        [
            StorageProfileTargetMapping.Create(SecondTargetId, "second-fast"),
            StorageProfileTargetMapping.Create(FirstTargetId, "first-standard")
        ]);

        Assert.Equal(1, profile.Revision);
        Assert.Equal(
            [FirstTargetId, SecondTargetId],
            profile.TargetMappings
                .Select(mapping => mapping.DeploymentTargetId)
                .ToArray());

        var document = profile.ToBsonDocument();
        Assert.Equal(2, document["MediaType"].AsInt32);
        Assert.Equal(2, document["PerformanceTier"].AsInt32);
        Assert.Equal(
            0,
            document["TenantAccess"].AsBsonDocument["Mode"].AsInt32);

        var roundTripped = BsonSerializer.Deserialize<StorageProfile>(
            profile.ToBson());
        Assert.Equal(profile.Id, roundTripped.Id);
        Assert.Equal(profile.Revision, roundTripped.Revision);
        Assert.Equal(profile.TargetMappings, roundTripped.TargetMappings);
        Assert.True(profile.AllowedResourceTypes.SetEquals(
            roundTripped.AllowedResourceTypes));
        Assert.Equal(profile.TenantAccess.Mode, roundTripped.TenantAccess.Mode);
    }

    [Fact]
    public void Factory_RejectsDuplicateTargetMappings()
    {
        Assert.Throws<ArgumentException>(() => CreateProfile(
        [
            StorageProfileTargetMapping.Create(FirstTargetId, "first-standard"),
            StorageProfileTargetMapping.Create(FirstTargetId, "first-fast")
        ]));
    }

    [Fact]
    public void Reconfigure_NoOpPreservesRevision_AndMappingChangeAdvancesIt()
    {
        var profile = CreateProfile(
        [
            StorageProfileTargetMapping.Create(FirstTargetId, "first-standard")
        ]);

        var unchanged = profile.Reconfigure(
            profile.DisplayName,
            profile.Description,
            profile.MediaType,
            profile.PerformanceTier,
            profile.IsEnabled,
            profile.IsDefault,
            profile.TargetMappings.Reverse(),
            profile.AllowedResourceTypes.Select(ResourceTypeId.Create),
            profile.TenantAccess,
            profile.SizeLimits,
            CreatedAtUtc.AddMinutes(1));
        Assert.Equal(profile.Revision, unchanged.Revision);
        Assert.Equal(profile.UpdatedAtUtc, unchanged.UpdatedAtUtc);

        var updated = profile.Reconfigure(
            profile.DisplayName,
            profile.Description,
            profile.MediaType,
            profile.PerformanceTier,
            profile.IsEnabled,
            profile.IsDefault,
            [
                StorageProfileTargetMapping.Create(
                    FirstTargetId,
                    "first-fast")
            ],
            profile.AllowedResourceTypes.Select(ResourceTypeId.Create),
            profile.TenantAccess,
            profile.SizeLimits,
            CreatedAtUtc.AddMinutes(1));
        Assert.Equal(profile.Revision + 1, updated.Revision);
        Assert.Equal(
            "first-fast",
            Assert.Single(updated.TargetMappings).StorageClassName);
    }

    private static StorageProfile CreateProfile(
        IEnumerable<StorageProfileTargetMapping> targetMappings) =>
        StorageProfileFactory.Create(
            technicalName: "storage-" + MongoDB.Bson.ObjectId.GenerateNewId(),
            "Domain test",
            "Domain test profile",
            StorageMediaType.SSD,
            StoragePerformanceTier.Fast,
            isEnabled: true,
            isDefault: false,
            targetMappings,
            [ResourceTypeId.Create("third-party/widget")],
            StorageProfileTenantAccess.Create(
                StorageProfileTenantAccessMode.AllTenants,
                []),
            StorageProfileSizeLimits.Create(1, 100),
            new StorageProfileFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                CreatedAtUtc));
}
