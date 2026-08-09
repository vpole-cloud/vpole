using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Xunit;

namespace Pole.Tests.Integration.ComputeProfiles;

public sealed class ComputeProfileDomainTests
{
    private const string FirstTargetId = "507f1f77bcf86cd799439096";
    private const string SecondTargetId = "507f1f77bcf86cd799439097";
    private const string TenantId = "507f1f77bcf86cd799439098";
    private static readonly DateTime CreatedAtUtc =
        new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Factory_NormalizesPortableConfiguration_AndRoundTripsBson()
    {
        var profile = CreateProfile(
        [
            Mapping(SecondTargetId),
            Mapping(FirstTargetId)
        ],
        SelectedTenantsComputeProfileAccess.Create([TenantId]));

        Assert.Equal(1, profile.Revision);
        Assert.Equal(0, profile.ResolutionFence);
        Assert.Equal(
            [FirstTargetId, SecondTargetId],
            profile.TargetMappings.Select(mapping => mapping.DeploymentTargetId));
        Assert.Equal(
            ["example.com/fpga", "nvidia.com/gpu"],
            profile.ExtendedResources.Select(resource => resource.Name));

        var document = profile.ToBsonDocument();
        Assert.Equal(1, document["Architecture"].AsInt32);
        var firstMapping = document["TargetMappings"].AsBsonArray[0].AsBsonDocument;
        Assert.Equal(
            0,
            firstMapping["RequiredNodeAffinity"].AsBsonArray[0]
                .AsBsonDocument["Expressions"].AsBsonArray[0]
                .AsBsonDocument["Operator"].AsInt32);
        Assert.Equal(
            1,
            firstMapping["Tolerations"].AsBsonArray[0]
                .AsBsonDocument["Operator"].AsInt32);
        Assert.Equal(
            1,
            firstMapping["Tolerations"].AsBsonArray[0]
                .AsBsonDocument["Effect"].AsInt32);

        var roundTripped = BsonSerializer.Deserialize<ComputeProfile>(
            profile.ToBson());

        Assert.Equal(profile.Id, roundTripped.Id);
        Assert.Equal(profile.Resources, roundTripped.Resources);
        Assert.Equal(
            profile.TargetMappings.Select(mapping => mapping.DeploymentTargetId),
            roundTripped.TargetMappings.Select(mapping => mapping.DeploymentTargetId));
        Assert.Equal(
            profile.TargetMappings.Select(mapping => mapping.RuntimeClassName),
            roundTripped.TargetMappings.Select(mapping => mapping.RuntimeClassName));
        Assert.Equal(
            profile.TargetMappings.SelectMany(mapping => mapping.NodeSelector),
            roundTripped.TargetMappings.SelectMany(mapping => mapping.NodeSelector));
        Assert.Equal(profile.ExtendedResources, roundTripped.ExtendedResources);
        Assert.IsType<SelectedTenantsComputeProfileAccess>(roundTripped.TenantAccess);
    }

    [Fact]
    public void Factory_RejectsDuplicateTargetAndExtendedResourceIdentities()
    {
        Assert.Throws<ArgumentException>(() => CreateProfile(
        [
            Mapping(FirstTargetId),
            Mapping(FirstTargetId)
        ],
        AllTenantsComputeProfileAccess.Create()));

        Assert.Throws<ArgumentException>(() => ComputeProfileFactory.Create(
            "Duplicate devices",
            string.Empty,
            Resources(),
            ComputeArchitecture.Any,
            [
                ComputeProfileExtendedResource.Create("nvidia.com/gpu", 1),
                ComputeProfileExtendedResource.Create("nvidia.com/gpu", 2)
            ],
            [ResourceTypeId.Create("pole/web-service")],
            AllTenantsComputeProfileAccess.Create(),
            [Mapping(FirstTargetId)],
            isEnabled: true,
            isDefault: false,
            Options()));
    }

    [Fact]
    public void Resources_RequirePositiveValues_AndLimitsAtLeastRequests()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ComputeProfileResourceQuantity.Create(0, 256));
        Assert.Throws<ArgumentException>(() =>
            ComputeProfileResourceRequirements.Create(
                ComputeProfileResourceQuantity.Create(500, 512),
                ComputeProfileResourceQuantity.Create(250, 1024)));
    }

    [Fact]
    public void Affinity_ValidatesOperatorShapes_AndNormalizesTerms()
    {
        var expression = ComputeProfileNodeAffinityExpression.Create(
            "kubernetes.io/arch",
            ComputeNodeAffinityOperator.In,
            ["arm64", "amd64", "arm64"]);
        Assert.Equal(["amd64", "arm64"], expression.Values);

        Assert.Throws<ArgumentException>(() =>
            ComputeProfileNodeAffinityExpression.Create(
                "pool",
                ComputeNodeAffinityOperator.Exists,
                ["general"]));
        Assert.Throws<ArgumentException>(() =>
            ComputeProfileNodeAffinityExpression.Create(
                "generation",
                ComputeNodeAffinityOperator.GreaterThan,
                ["not-a-number"]));
    }

    [Fact]
    public void Toleration_RequiresCompatibleOperatorValueEffectAndSeconds()
    {
        var toleration = ComputeProfileToleration.Create(
            "workload",
            ComputeTolerationOperator.Equal,
            "gpu",
            ComputeTaintEffect.NoExecute,
            tolerationSeconds: 60);
        Assert.Equal(60, toleration.TolerationSeconds);

        var immediateEmptyValue = ComputeProfileToleration.Create(
            "workload",
            ComputeTolerationOperator.Equal,
            string.Empty,
            ComputeTaintEffect.NoExecute,
            tolerationSeconds: 0);
        Assert.Equal(string.Empty, immediateEmptyValue.Value);
        Assert.Equal(0, immediateEmptyValue.TolerationSeconds);

        Assert.Throws<ArgumentException>(() => ComputeProfileToleration.Create(
            "workload",
            ComputeTolerationOperator.Exists,
            "gpu",
            ComputeTaintEffect.NoSchedule,
            null));
        Assert.Throws<ArgumentException>(() => ComputeProfileToleration.Create(
            "workload",
            ComputeTolerationOperator.Equal,
            "gpu",
            ComputeTaintEffect.NoSchedule,
            60));
    }

    [Fact]
    public void Reconfigure_NoOpPreservesRevision_AndEffectiveChangeAdvancesIt()
    {
        var profile = CreateProfile(
            [Mapping(FirstTargetId)],
            AllTenantsComputeProfileAccess.Create());

        var unchanged = profile.Reconfigure(
            profile.DisplayName,
            profile.Description,
            profile.Resources,
            profile.Architecture,
            profile.ExtendedResources.Reverse(),
            profile.AllowedResourceTypes
                .Select(ResourceTypeId.Create)
                .Reverse(),
            profile.TenantAccess,
            profile.TargetMappings.Reverse(),
            profile.IsEnabled,
            profile.IsDefault,
            CreatedAtUtc.AddMinutes(1));
        Assert.Same(profile, unchanged);

        var updated = profile.Reconfigure(
            profile.DisplayName,
            "Changed description",
            profile.Resources,
            profile.Architecture,
            profile.ExtendedResources,
            profile.AllowedResourceTypes.Select(ResourceTypeId.Create),
            profile.TenantAccess,
            profile.TargetMappings,
            profile.IsEnabled,
            profile.IsDefault,
            CreatedAtUtc.AddMinutes(1));
        Assert.Equal(2, updated.Revision);
        Assert.Equal("Changed description", updated.Description);
    }

    private static ComputeProfile CreateProfile(
        IEnumerable<ComputeProfileTargetMapping> mappings,
        ComputeProfileTenantAccess tenantAccess) =>
        ComputeProfileFactory.Create(
            "Accelerated",
            "Portable test profile",
            Resources(),
            ComputeArchitecture.Amd64,
            [
                ComputeProfileExtendedResource.Create("nvidia.com/gpu", 1),
                ComputeProfileExtendedResource.Create("example.com/fpga", 2)
            ],
            [
                ResourceTypeId.Create("pole/web-service"),
                ResourceTypeId.Create("pole/postgresql")
            ],
            tenantAccess,
            mappings,
            isEnabled: true,
            isDefault: false,
            Options());

    private static ComputeProfileResourceRequirements Resources() =>
        ComputeProfileResourceRequirements.Create(
            ComputeProfileResourceQuantity.Create(250, 256),
            ComputeProfileResourceQuantity.Create(1000, 1024));

    private static ComputeProfileTargetMapping Mapping(string targetId)
    {
        var requiredTerm = ComputeProfileNodeAffinityTerm.Create(
        [
            ComputeProfileNodeAffinityExpression.Create(
                "pole.io/node-pool",
                ComputeNodeAffinityOperator.In,
                ["accelerated"])
        ]);
        return ComputeProfileTargetMapping.Create(
            targetId,
            [ComputeProfileNodeSelector.Create("kubernetes.io/os", "linux")],
            [requiredTerm],
            [ComputeProfilePreferredNodeAffinityTerm.Create(50, requiredTerm)],
            [ComputeProfileToleration.Create(
                "accelerated",
                ComputeTolerationOperator.Exists,
                null,
                ComputeTaintEffect.NoSchedule,
                null)],
            "kata");
    }

    private static ComputeProfileFactory.CreateOptions Options() =>
        new(ObjectId.GenerateNewId().ToString(), CreatedAtUtc);
}
