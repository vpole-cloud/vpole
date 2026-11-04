using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.ResourceGroups.Domain;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.ResourceGroups;

public sealed class ResourceGroupDomainTests
{
    private const string TenantId = "507f1f77bcf86cd799439011";
    private const string ResourceGroupId = "507f1f77bcf86cd799439012";
    private const string ScopeId = "507f1f77bcf86cd799439013";
    private const string TargetId = "507f1f77bcf86cd799439014";
    private static readonly DateTime CreatedAtUtc =
        new(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IsolationScopeFactory_UsesTechnicalName_AndRejectsReservedDefault()
    {
        var scope = IsolationScopeFactory.CreateAdditional(
            TenantId,
            ResourceGroupId,
            "production",
            new IsolationScopeFactory.CreateOptions(ScopeId, CreatedAtUtc));

        Assert.Equal("production", scope.Name);
        Assert.Equal("production", scope.NormalizedName);
        Assert.Throws<ArgumentException>(() => IsolationScopeFactory.CreateAdditional(
            TenantId,
            ResourceGroupId,
            "DEFAULT",
            new IsolationScopeFactory.CreateOptions(ScopeId, CreatedAtUtc)));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData(" production ")]
    [InlineData("1production")]
    [InlineData("production_")]
    public void IsolationScopeFactory_RejectsInvalidTechnicalNames(string name) =>
        Assert.Throws<ArgumentException>(() => IsolationScopeFactory.CreateAdditional(
            TenantId, ResourceGroupId, name, new IsolationScopeFactory.CreateOptions(ScopeId, CreatedAtUtc)));

    [Fact]
    public void IsolationScopeCurrentStatesRoundTripBsonWithExplicitInitialValues()
    {
        var scope = IsolationScopeFactory.CreateAdditional(TenantId, ResourceGroupId, "production",
            new IsolationScopeFactory.CreateOptions(ScopeId, CreatedAtUtc));
        Assert.Equal(1, scope.Revision);
        Assert.False(scope.IsDefault);
        Assert.IsType<ActiveIsolationScopeLifecycleState>(scope.Lifecycle);
        Assert.Equal(scope, BsonSerializer.Deserialize<IsolationScope>(scope.ToBson()));
        var deleting = scope.RequestDeletion(CreatedAtUtc.AddMinutes(1));
        Assert.Equal(2, deleting.Revision);
        Assert.Equal(deleting, BsonSerializer.Deserialize<IsolationScope>(deleting.ToBson()));
    }

    [Fact]
    public void DefaultIsolationScope_CannotBeDeletedSeparately()
    {
        var scope = IsolationScopeFactory.CreateDefault(
            TenantId,
            ResourceGroupId,
            new IsolationScopeFactory.CreateOptions(ScopeId, CreatedAtUtc));

        Assert.Throws<InvalidOperationException>(() =>
            scope.RequestDeletion(CreatedAtUtc.AddMinutes(1)));
    }

    [Fact]
    public void ResourceGroupMetadataUpdate_IncrementsRevisionAndPreservesOwnershipIdentity()
    {
        var resourceGroup = ResourceGroupFactory.Create(
            TenantId,
            "apps",
            "before",
            ResourceGroupFactory.DefaultCreateOptions with
            {
                Id = ResourceGroupId,
                DefaultIsolationScopeId = ScopeId,
                CreatedAtUtc = CreatedAtUtc
            });

        var updated = resourceGroup.UpdateMetadata(
            "after",
            ["production"],
            CreatedAtUtc.AddMinutes(1));

        Assert.Equal(resourceGroup.Revision + 1, updated.Revision);
        Assert.Equal(resourceGroup.DefaultIsolationScopeId, updated.DefaultIsolationScopeId);
        Assert.Equal("after", updated.Description);
        Assert.Contains("production", updated.Tags);
    }

    [Fact]
    public void ResourceRegistration_ValidatesTypeOwnership_AndRoundTripsBson()
    {
        var registration = ResourceRegistrationFactory.Create(
            TenantId,
            ResourceGroupId,
            ResourceTypeId.Create("acme/database"),
            "external-42",
            "Orders database",
            "acme",
            new ResourceRegistrationFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                CreatedAtUtc));

        var roundTripped = BsonSerializer.Deserialize<ResourceRegistration>(
            registration.ToBson());

        Assert.Equal("acme/database", roundTripped.ResourceTypeId);
        Assert.Equal("acme", roundTripped.OwningPluginId);
        Assert.Throws<ArgumentException>(() => ResourceRegistrationFactory.Create(
            TenantId,
            ResourceGroupId,
            ResourceTypeId.Create("other/database"),
            "external-42",
            "Orders database",
            "acme",
            new ResourceRegistrationFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                CreatedAtUtc)));
        Assert.Throws<ArgumentException>(() => ResourceRegistrationFactory.Create(
            TenantId,
            ResourceGroupId,
            ResourceTypeId.Create("third-party/widget"),
            "external-42",
            "Redis",
            "pole",
            new ResourceRegistrationFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                CreatedAtUtc)));
    }

    [Fact]
    public void RuntimeCell_RejectsStaleGeneration_AndRoundTripsPolymorphicState()
    {
        var cell = RuntimeCellFactory.Create(
            TenantId,
            ResourceGroupId,
            ScopeId,
            TargetId,
            "pole-a1b2c3",
            new RuntimeCellFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                CreatedAtUtc));
        var ready = cell.MarkReady(1, CreatedAtUtc.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(() =>
            ready.MarkReady(1, CreatedAtUtc.AddMinutes(2)));

        var roundTripped = BsonSerializer.Deserialize<RuntimeCell>(ready.ToBson());
        var readyState = Assert.IsType<ReadyRuntimeCellState>(roundTripped.State);
        Assert.Equal(1, readyState.Generation);
        Assert.Equal(ready.KubernetesNamespace, roundTripped.KubernetesNamespace);
    }
}
