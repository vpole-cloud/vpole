using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Tests.Integration.Infrastructure;
using Pole.Tests.Integration.DeploymentTargets;
using Pole.Tests.Integration.ResourceDeployments;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Global)]
public sealed class PlanningDependencyGuardsTests(MongoReplicaSetFixture fixture)
    : IsolatedMediatorIntegrationTestBase(fixture)
{
    private static readonly ResourceTypeId ResourceType = ResourceTypeId.Create("pole/web-service");
    private IMongoCollection<ComputeProfile> Profiles =>
        Fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles");
    private IMongoCollection<DeploymentTarget> Targets =>
        Fixture.Database.GetCollection<DeploymentTarget>("DeploymentTargets");
    private IMongoCollection<ResourceRegistration> Registrations =>
        Fixture.Database.GetCollection<ResourceRegistration>("ResourceRegistrations");

    protected override void ConfigureServices(IServiceCollection services)
    {
        // The shared mediator host adds a fake after production registration. These tests
        // must exercise the original scoped Mongo fence and its real conditional write.
        foreach (var descriptor in services.Where(item =>
                     item.ServiceType == typeof(FenceDeploymentTargetWorkloadConfigurations) &&
                     item.Lifetime == ServiceLifetime.Singleton).ToArray())
            services.Remove(descriptor);
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await Fixture.PrepareNamedCatalogAsync();
        await Targets.ReplaceOneAsync(item => item.Id == TestData.DefaultDeploymentTargetId,
            TestData.SeedDeploymentTarget().AttestForTests());
    }

    [Fact]
    public async Task PlanningReads_RequireAnActiveTransaction()
    {
        using var session = await Fixture.Client.StartSessionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await GroupRead(new(Data.TenantId, Id()), session, default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await PlacementRead(new(Data.TenantId, ResourceType, Id()), session, default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ComputeRead(ComputeRequest(), session, default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await TargetRead(TargetRequest(), session, default));
    }

    [Fact]
    public async Task SuccessfulGuards_ChangeOnlyTechnicalFences()
    {
        var (group, placement, registration) = await SeedPlacementAsync();
        var target = await Targets.Find(item => item.Id == TestData.DefaultDeploymentTargetId).SingleAsync();
        var profile = await Profiles.Find(item => item.Id == TestData.DefaultComputeProfileId).SingleAsync();
        var outboxCount = await DbContext.OutboxMessages.CountDocumentsAsync(_ => true);

        await InTransaction(async session =>
        {
            var groupSnapshot = Assert.IsType<ResourceGroupPlanningSnapshot>((await GroupRead(
                new(Data.TenantId, group.Id), session, default)).Value);
            var placements = Assert.IsType<ResourceDeploymentPlanningSnapshot>((await PlacementRead(
                new(Data.TenantId, ResourceType, placement.ResourceId), session, default)).Value);
            var compute = Assert.IsType<ComputeProfilePlanningSnapshot>((await ComputeRead(
                ComputeRequest(), session, default)).Value);
            var targetSnapshot = Assert.IsType<DeploymentTargetPlanningSnapshot>((await TargetRead(
                TargetRequest(), session, default)).Value);

            Assert.Equal(group.DefaultIsolationScopeId, groupSnapshot.DefaultIsolationScopeId);
            Assert.Equal(1, groupSnapshot.ScopeRevision);
            var planned = Assert.Single(placements.Items);
            Assert.Equal(placement.Id, planned.DeploymentId);
            Assert.Equal(placement.DesiredGeneration, planned.DesiredGeneration);
            Assert.Equal(placement.ResourceRevision, planned.ResourceRevision);
            Assert.Equal(ResourceDeploymentPlanningState.Prepared, planned.State);
            Assert.Equal(3, planned.FixedReplicas);
            Assert.Equal(250, planned.Compute.Requests.CpuMillis);
            Assert.IsType<ResourceGroupPlanningFenced>((await GroupFence(groupSnapshot, session, default)).Value);
            Assert.IsType<ResourceDeploymentPlanningFenced>((await PlacementFence(placements, session, default)).Value);
            Assert.IsType<ComputeProfilePlanningFenced>((await ComputeFence(compute, session, default)).Value);
            Assert.IsType<DeploymentTargetPlanningFenced>((await TargetFence(targetSnapshot, session, default)).Value);
            return true;
        });

        var savedGroup = await DbContext.ResourceGroups.Find(item => item.Id == group.Id).SingleAsync();
        var savedPlacement = await DbContext.ResourceDeployments.Find(item => item.Id == placement.Id).SingleAsync();
        var savedRegistration = await Registrations.Find(item => item.Id == registration.Id).SingleAsync();
        var savedProfile = await Profiles.Find(item => item.Id == profile.Id).SingleAsync();
        var savedTarget = await Targets.Find(item => item.Id == target.Id).SingleAsync();
        Assert.Equal(group.RegistrationFence + 1, savedGroup.RegistrationFence);
        Assert.Equal(placement.ReferenceFence + 1, savedPlacement.ReferenceFence);
        Assert.Equal(registration.ReferenceFence + 1, savedRegistration.ReferenceFence);
        Assert.Equal(profile.ResolutionFence + 1, savedProfile.ResolutionFence);
        Assert.Equal(target.ReferenceFence + 1, savedTarget.ReferenceFence);
        SameBusinessData(group, savedGroup with { RegistrationFence = group.RegistrationFence });
        SameBusinessData(placement, savedPlacement with { ReferenceFence = placement.ReferenceFence });
        SameBusinessData(registration, savedRegistration with { ReferenceFence = registration.ReferenceFence });
        SameBusinessData(profile, savedProfile with { ResolutionFence = profile.ResolutionFence });
        SameBusinessData(target, savedTarget with { ReferenceFence = target.ReferenceFence });
        Assert.Equal(outboxCount,
            await DbContext.OutboxMessages.CountDocumentsAsync(_ => true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupGuard_RejectsChangedGroupOrDefaultScopeVersion(bool changeScope)
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var snapshot = await InTransaction(async session => Assert.IsType<ResourceGroupPlanningSnapshot>(
            (await GroupRead(new(Data.TenantId, group.Id), session, default)).Value));
        if (changeScope)
            await DbContext.IsolationScopes.UpdateOneAsync(item => item.Id == group.DefaultIsolationScopeId,
                Builders<IsolationScope>.Update.Inc(item => item.Revision, 1));
        else
            await DbContext.ResourceGroups.UpdateOneAsync(item => item.Id == group.Id,
                Builders<ResourceGroup>.Update.Inc(item => item.Revision, 1));

        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        Assert.IsType<ResourceGroupPlanningFenceRejected>((await GroupFence(snapshot, session, default)).Value);
        await session.AbortTransactionAsync();
        Assert.Equal(group.RegistrationFence,
            (await DbContext.ResourceGroups.Find(item => item.Id == group.Id).SingleAsync()).RegistrationFence);
    }

    [Fact]
    public async Task GroupAndPlacementReads_DoNotCrossTenantBoundary()
    {
        var (group, placement, _) = await SeedPlacementAsync();
        await InTransaction(async session =>
        {
            var foreignTenant = Id();
            Assert.IsType<ResourceGroupPlanningUnavailable>((await GroupRead(
                new(foreignTenant, group.Id), session, default)).Value);
            Assert.IsType<ResourceDeploymentPlanningUnavailable>((await PlacementRead(
                new(foreignTenant, ResourceType, placement.ResourceId), session, default)).Value);
            return true;
        });
    }

    [Fact]
    public async Task ComputeSnapshot_PreservesPinnedRevisionAndFencesCurrentPolicyRevision()
    {
        var original = await Profiles.Find(item => item.Id == TestData.DefaultComputeProfileId).SingleAsync();
        await Profiles.ReplaceOneAsync(item => item.Id == original.Id, original with { Revision = 2 });
        var snapshot = await InTransaction(async session => Assert.IsType<ComputeProfilePlanningSnapshot>(
            (await ComputeRead(ComputeRequest(), session, default)).Value));
        Assert.Equal(1, snapshot.Compute.ComputeProfileRevision);
        Assert.Equal(2, snapshot.PolicyRevision);
        await InTransaction(async session =>
            Assert.IsType<ComputeProfilePlanningFenced>((await ComputeFence(snapshot, session, default)).Value));

        await Profiles.UpdateOneAsync(item => item.Id == original.Id,
            Builders<ComputeProfile>.Update.Inc(item => item.Revision, 1));
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        Assert.IsType<ComputeProfilePlanningFenceRejected>((await ComputeFence(snapshot, session, default)).Value);
        await session.AbortTransactionAsync();
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("resource-type")]
    [InlineData("target")]
    public async Task PinnedComputeSnapshot_PreservesCurrentAdmission(string changed)
    {
        var profile = await Profiles.Find(item => item.Id == TestData.DefaultComputeProfileId).SingleAsync();
        await Profiles.ReplaceOneAsync(item => item.Id == profile.Id, profile with
        {
            TenantAccess = SelectedTenantsComputeProfileAccess.Create([Data.TenantId]),
            Revision = 2
        });
        var request = changed switch
        {
            "tenant" => ComputeRequest() with { TenantId = Id() },
            "resource-type" => ComputeRequest() with { ResourceType = ResourceTypeId.Create("other/resource") },
            "target" => ComputeRequest() with { DeploymentTargetId = Id() },
            _ => throw new ArgumentOutOfRangeException(nameof(changed))
        };
        await InTransaction(async session =>
        {
            var unavailable = Assert.IsType<ComputeProfilePlanningUnavailable>(
                (await ComputeRead(request, session, default)).Value);
            var detail = Assert.IsType<ComputeProfileUnavailable>(unavailable.Error.Value);
            Assert.Equal(changed switch
            {
                "tenant" => ComputeProfileUnavailableReason.TenantNotAllowed,
                "resource-type" => ComputeProfileUnavailableReason.ResourceTypeNotAllowed,
                _ => ComputeProfileUnavailableReason.DeploymentTargetNotConfigured
            }, detail.Reason);
            return true;
        });
    }

    [Theory]
    [InlineData("admission")]
    [InlineData("configuration")]
    [InlineData("revision")]
    [InlineData("network-management")]
    public async Task TargetGuard_RejectsChangedAdmissionOrExpectedVersions(string changed)
    {
        var snapshot = await InTransaction(async session => Assert.IsType<DeploymentTargetPlanningSnapshot>(
            (await TargetRead(TargetRequest(), session, default)).Value));
        var update = changed switch
        {
            "admission" => Builders<DeploymentTarget>.Update.Set(item => item.Enabled, false),
            "configuration" => Builders<DeploymentTarget>.Update.Inc(item => item.WorkloadConfiguration.Revision, 1),
            "network-management" => Builders<DeploymentTarget>.Update.Set(item => item.Capabilities,
                DeploymentTargetCapabilities.Unknown(1)),
            _ => Builders<DeploymentTarget>.Update.Inc(item => item.Revision, 1)
        };
        await Targets.UpdateOneAsync(item => item.Id == snapshot.Target.Id, update);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        Assert.IsType<DeploymentTargetPlanningFenceRejected>((await TargetFence(snapshot, session, default)).Value);
        await session.AbortTransactionAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlacementGuard_RejectsChangedVersionOrExactSet(bool insertPlacement)
    {
        var (_, deployment, registration) = await SeedPlacementAsync();
        var snapshot = await ReadPlacements(deployment.ResourceId);
        if (insertPlacement)
            await DbContext.ResourceDeployments.InsertOneAsync(GitOpsPlacementTestData.AdditionalPlacement(deployment));
        else
            await DbContext.ResourceDeployments.UpdateOneAsync(item => item.Id == deployment.Id,
                Builders<ResourceDeployment>.Update.Inc(item => item.Revision, 1));

        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        Assert.IsType<ResourceDeploymentPlanningFenceRejected>(
            (await PlacementFence(snapshot, session, default)).Value);
        await session.AbortTransactionAsync();
        Assert.Equal(registration.ReferenceFence,
            (await Registrations.Find(item => item.Id == registration.Id).SingleAsync()).ReferenceFence);
    }

    [Fact]
    public async Task PlacementRead_ConflictsWithConcurrentRegisteredInsertion()
    {
        var (_, deployment, _) = await SeedPlacementAsync();
        using var reader = await Fixture.Client.StartSessionAsync();
        reader.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot));
        var snapshot = Assert.IsType<ResourceDeploymentPlanningSnapshot>((await PlacementRead(
            new(Data.TenantId, ResourceType, deployment.ResourceId), reader, default)).Value);

        await InTransaction(async writer =>
        {
            Assert.NotNull(await Services.GetRequiredService<FenceRegisteredResource>()(
                new(Data.TenantId, ResourceType, deployment.ResourceId), writer, default));
            await DbContext.ResourceDeployments.InsertOneAsync(writer, GitOpsPlacementTestData.AdditionalPlacement(deployment));
            return true;
        });

        var error = await Assert.ThrowsAnyAsync<MongoException>(async () =>
            await PlacementFence(snapshot, reader, default));
        Assert.True(error.HasErrorLabel("TransientTransactionError"));
        await reader.AbortTransactionAsync();
    }

    [Fact]
    public async Task RejectedLaterGuard_RollsBackEarlierFences()
    {
        var (group, deployment, _) = await SeedPlacementAsync();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var groupSnapshot = Assert.IsType<ResourceGroupPlanningSnapshot>((await GroupRead(
            new(Data.TenantId, group.Id), session, default)).Value);
        var placements = Assert.IsType<ResourceDeploymentPlanningSnapshot>((await PlacementRead(
            new(Data.TenantId, ResourceType, deployment.ResourceId), session, default)).Value);
        var stale = placements with { Items = [placements.Items[0] with { Revision = 100 }] };
        Assert.IsType<ResourceGroupPlanningFenced>((await GroupFence(groupSnapshot, session, default)).Value);
        Assert.IsType<ResourceDeploymentPlanningFenceRejected>((await PlacementFence(stale, session, default)).Value);
        await session.AbortTransactionAsync();
        SameBusinessData(group, await DbContext.ResourceGroups.Find(item => item.Id == group.Id).SingleAsync());
        SameBusinessData(deployment,
            await DbContext.ResourceDeployments.Find(item => item.Id == deployment.Id).SingleAsync());
    }

    private LoadResourceGroupPlanningSnapshot GroupRead =>
        Services.GetRequiredService<LoadResourceGroupPlanningSnapshot>();
    private FenceResourceGroupPlanningSnapshot GroupFence =>
        Services.GetRequiredService<FenceResourceGroupPlanningSnapshot>();
    private LoadResourceDeploymentPlanningSnapshot PlacementRead =>
        Services.GetRequiredService<LoadResourceDeploymentPlanningSnapshot>();
    private FenceResourceDeploymentPlanningSnapshot PlacementFence =>
        Services.GetRequiredService<FenceResourceDeploymentPlanningSnapshot>();
    private ResolveComputeProfilePlanningSnapshot ComputeRead =>
        Services.GetRequiredService<ResolveComputeProfilePlanningSnapshot>();
    private FenceComputeProfilePlanningSnapshot ComputeFence =>
        Services.GetRequiredService<FenceComputeProfilePlanningSnapshot>();
    private ResolveDeploymentTargetPlanningSnapshot TargetRead =>
        Services.GetRequiredService<ResolveDeploymentTargetPlanningSnapshot>();
    private FenceDeploymentTargetPlanningSnapshot TargetFence =>
        Services.GetRequiredService<FenceDeploymentTargetPlanningSnapshot>();

    private static string Id() => ObjectId.GenerateNewId().ToString();

    private ComputeProfilePlanningRequest ComputeRequest() =>
        new(Data.TenantId, ResourceType, TestData.DefaultComputeProfileId, TestData.DefaultDeploymentTargetId, 1);

    private DeploymentTargetAdmissionRequest TargetRequest() =>
        new(Data.TenantId, ResourceType, null, TestData.DefaultDeploymentTargetId,
            DeploymentTargetRequiredCapabilities.None);

    private Task<ResourceDeploymentPlanningSnapshot> ReadPlacements(string resourceId) =>
        InTransaction(async session => Assert.IsType<ResourceDeploymentPlanningSnapshot>((await PlacementRead(
            new(Data.TenantId, ResourceType, resourceId), session, default)).Value));

    private async Task<T> InTransaction<T>(Func<IClientSessionHandle, Task<T>> action)
    {
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot));
        var result = await action(session);
        await session.CommitTransactionAsync();
        return result;
    }

    private static void SameBusinessData<T>(T expected, T actual) =>
        Assert.Equal(expected.ToBsonDocument(), actual.ToBsonDocument());

    private async Task<(ResourceGroup, ResourceDeployment, ResourceRegistration)> SeedPlacementAsync()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var resourceId = Id();
        var registration = ResourceRegistrationFactory.Create(Data.TenantId, group.Id, ResourceType,
            resourceId, "api", null, new(Id(), DateTime.UtcNow));
        await Registrations.InsertOneAsync(registration);
        var placement = ResourceDeploymentFactory.Create(Data.TenantId, ResourceType, resourceId, group.Id,
            group.DefaultIsolationScopeId,
            ResolvedResourcePlacement.Create(Id(), TestData.DefaultDeploymentTargetId, "planning-guards", 1, 1,
                null, null, ResourceDeploymentTestData.Compute(), DateTime.UtcNow),
            ResourcePlacementCapability.MultiTarget, runtimeCellReady: true,
            ResourceDeploymentFactory.DefaultOptions with { FixedReplicas = 3, DeferredActivation = true });
        await DbContext.ResourceDeployments.InsertOneAsync(placement);
        return (group, placement, registration);
    }
}
