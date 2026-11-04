using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Quotas.Domain;
using Pole.Features.Quotas.Primitives;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Endpoints.Deployments;
using Pole.Features.WebServices.Application.Endpoints.Releases;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

public sealed class WebServicePlacementQuotaTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_AfterRemovingTarget_ReleasesOnlyRemainingDesiredUsage(bool cleanupCompleted)
    {
        var secondTargetId = ObjectId.GenerateNewId().ToString();
        Services.GetRequiredService<TestDeploymentTargetLookup>()
            .AddEligibleTarget(secondTargetId, "apps.second.test.local");
        await Fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles")
            .ReplaceOneAsync(
                profile => profile.Id == TestData.DefaultComputeProfileId,
                TestData.SeedComputeProfile(deploymentTargetIds:
                    [TestData.DefaultDeploymentTargetId, secondTargetId]));
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var request = TestData.CreateWebServiceRequest(group.Id);
        var created = (await Mediator.Send(request with
        {
            Body = request.Body with
            {
                Placement = new WebServicePlacementRequest(null,
                [
                    new WebServiceDeploymentTargetRequest(TestData.DefaultDeploymentTargetId, 2),
                    new WebServiceDeploymentTargetRequest(secondTargetId, 3)
                ])
            }
        })).AssertSuccess();
        (await Mediator.Send(TestData.CreateWebServiceRequest(group.Id, "unrelated")))
            .AssertSuccess();
        await AssertUsageAsync(1750, 1792, 7, 2);

        var removed = created.Deployments.Single(deployment =>
            deployment.DeploymentTargetId == secondTargetId);
        (await Mediator.Send(new DeleteWebServiceDeploymentRequest(
            created.WebServiceId, removed.DeploymentId, TestData.TenantId)))
            .AssertSuccess();
        await AssertUsageAsync(1000, 1024, 4, 2);
        if (cleanupCompleted)
        {
            var deleting = await DbContext.ResourceDeployments
                .Find(item => item.Id == removed.DeploymentId).SingleAsync();
            var claimId = ObjectId.GenerateNewId().ToString();
            var started = deleting.UpdatedAtUtc.AddSeconds(1);
            var deleted = deleting.Start(deleting.DesiredGeneration, ResourceDeploymentOperation.Delete,
                    claimId, started, started.AddMinutes(1))
                .Complete(deleting.DesiredGeneration, ResourceDeploymentOperation.Delete,
                    claimId, [], started.AddSeconds(1));
            await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == deleted.Id, deleted);
        }

        (await Mediator.Send(new DeleteWebServiceRequest(
            created.WebServiceId, TestData.TenantId))).AssertSuccess();
        await AssertUsageAsync(500, 512, 2, 1);

        (await Mediator.Send(new DeleteWebServiceRequest(
            created.WebServiceId, TestData.TenantId))).AssertSuccess();
        await AssertUsageAsync(500, 512, 2, 1);
    }

    [Fact]
    public async Task SharedScaling_AfterRemovingTarget_ChargesOnlyDesiredDeployments()
    {
        var created = await CreateMultiTargetAsync();
        var removed = created.Deployments.Single(deployment => deployment.FixedReplicas == 3);
        (await Mediator.Send(new DeleteWebServiceDeploymentRequest(
            created.WebServiceId, removed.DeploymentId, TestData.TenantId)))
            .AssertSuccess();
        var scaling = TestData.ValidWebServiceSpec() with
        {
            Autoscaling = new AutoscalingSpecDto(1, 4, new AutoscalingMetricDto("cpu", 80))
        };

        var updated = (await Mediator.Send(new UpdateWebServiceRequest(
            created.WebServiceId,
            new UpdateWebServiceRequestBody(scaling, [], WebServiceExposureMode.Public),
            TestData.TenantId))).AssertSuccess();

        Assert.Single(updated.Deployments);
        Assert.DoesNotContain(updated.Deployments, item => item.DeploymentId == removed.DeploymentId);
        await AssertUsageAsync(1000, 1024, 4, 1);
        var retained = await DbContext.ResourceDeployments
            .Find(item => item.Id == removed.DeploymentId).SingleAsync();
        Assert.Equal(ResourceDeploymentOperation.Delete, retained.DesiredOperation);
        var release = await Fixture.Database.WebServiceReleases()
            .Find(item => item.WebServiceId == created.WebServiceId &&
                item.ResourceRevision == updated.ResourceRevision).SingleAsync();
        Assert.Single(release.Deployments);
    }

    [Fact]
    public async Task TargetMutations_ChargeResolvedProfilesAndReplicas_OncePerLogicalResource()
    {
        var created = await CreateMultiTargetAsync();
        await AssertUsageAsync(1250, 1280, 5, 1);
        var changed = created.Deployments.Single(deployment => deployment.FixedReplicas == 3);
        var profileId = ObjectId.GenerateNewId().ToString();
        await Fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles")
            .InsertOneAsync(TestData.SeedComputeProfile(
                id: profileId,
                requestedCpuMillis: 500,
                requestedMemoryMi: 768,
                isDefault: false,
                deploymentTargetIds: [changed.DeploymentTargetId]));

        (await Mediator.Send(new UpdateWebServiceDeploymentRequest(
            created.WebServiceId,
            changed.DeploymentId,
            new UpdateWebServiceDeploymentBody(4, profileId),
            TestData.TenantId))).AssertSuccess();
        await AssertUsageAsync(2500, 3584, 6, 1);

        var scaling = TestData.ValidWebServiceSpec() with
        {
            Autoscaling = new AutoscalingSpecDto(1, 5, new AutoscalingMetricDto("cpu", 80))
        };
        (await Mediator.Send(new UpdateWebServiceRequest(
            created.WebServiceId,
            new UpdateWebServiceRequestBody(scaling, [], WebServiceExposureMode.Public),
            TestData.TenantId))).AssertSuccess();
        await AssertUsageAsync(3750, 5120, 10, 1);

        (await Mediator.Send(new DeleteWebServiceDeploymentRequest(
            created.WebServiceId, changed.DeploymentId, TestData.TenantId)))
            .AssertSuccess();
        await AssertUsageAsync(1250, 1280, 5, 1);
        (await Mediator.Send(new DeleteWebServiceDeploymentRequest(
            created.WebServiceId, changed.DeploymentId, TestData.TenantId)))
            .AssertFailure();
        await AssertUsageAsync(1250, 1280, 5, 1);

        var thirdTargetId = ObjectId.GenerateNewId().ToString();
        Services.GetRequiredService<TestDeploymentTargetLookup>()
            .AddEligibleTarget(thirdTargetId, "apps.third.test.local");
        var thirdProfileId = ObjectId.GenerateNewId().ToString();
        await Fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles")
            .InsertOneAsync(TestData.SeedComputeProfile(
                id: thirdProfileId,
                requestedCpuMillis: 750,
                requestedMemoryMi: 1024,
                isDefault: false,
                deploymentTargetIds: [thirdTargetId]));
        (await Mediator.Send(new AddWebServiceDeploymentRequest(
            created.WebServiceId,
            new WebServiceDeploymentTargetRequest(thirdTargetId, 2, thirdProfileId),
            TestData.TenantId))).AssertSuccess();
        await AssertUsageAsync(5000, 6400, 10, 1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task StaleUpdate_AfterConcurrentRemoval_RollsBackQuotaAndCancelsReservation(int replicas)
    {
        var created = await CreateMultiTargetAsync();
        var remaining = created.Deployments.Single(deployment => deployment.FixedReplicas == 2);
        var removed = created.Deployments.Single(deployment => deployment.FixedReplicas == 3);
        var load = Services.GetRequiredService<LoadResourceDeploymentSet>();
        LoadResourceDeploymentSet interleavedLoad = async (request, token) =>
        {
            var stale = await load(
                new LoadResourceDeploymentSetRequest(
                    TenantId: request.TenantId,
                    ResourceType: request.ResourceType,
                    ResourceId: request.ResourceId),
                cancellationToken: token);
            (await Mediator.Send(new DeleteWebServiceDeploymentRequest(
                created.WebServiceId, removed.DeploymentId, TestData.TenantId)))
                .AssertSuccess();
            return stale;
        };
        var handler = ActivatorUtilities.CreateInstance<UpdateWebServiceDeploymentHandler>(
            Services, interleavedLoad);

        var result = await handler.HandleAsync(new UpdateWebServiceDeploymentRequest(
            created.WebServiceId,
            remaining.DeploymentId,
            new UpdateWebServiceDeploymentBody(replicas),
            TestData.TenantId));

        Assert.IsType<UpdateWebServiceDeploymentError>(result.Value);
        await AssertUsageAsync(500, 512, 2, 1);
        var actual = await DbContext.ResourceDeployments
            .Find(item => item.Id == remaining.DeploymentId).SingleAsync();
        Assert.Equal(2, actual.FixedReplicas);
        if (replicas > remaining.FixedReplicas)
        {
            Assert.Equal(1, await Fixture.Database.GetCollection<QuotaReservation>("QuotaReservations")
                .CountDocumentsAsync(item => item.TenantId == TestData.TenantId &&
                    item.State.Kind == QuotaReservationStateKinds.Cancelled));
        }
        Assert.Equal(0, await Fixture.Database.GetCollection<QuotaReservation>("QuotaReservations")
            .CountDocumentsAsync(item => item.TenantId == TestData.TenantId &&
                item.State.Kind == QuotaReservationStateKinds.Active));
    }

    [Theory]
    [InlineData("redeploy")]
    [InlineData("rollback")]
    [InlineData("image-tag")]
    public async Task Release_AfterRemovingTarget_AppliesOnlyDesiredPlacements(string action)
    {
        var created = await CreateMultiTargetAsync();
        await Fixture.Database.WebServiceReleases().UpdateOneAsync(
            item => item.WebServiceId == created.WebServiceId && item.ResourceRevision == 1,
            Builders<WebServiceRelease>.Update
                .Set(item => item.Outcome, WebServiceReleaseOutcome.Succeeded)
                .Set(item => item.CompletedAtUtc, DateTime.UtcNow));
        var removed = created.Deployments.Single(item => item.FixedReplicas == 3);
        (await Mediator.Send(new DeleteWebServiceDeploymentRequest(
            created.WebServiceId, removed.DeploymentId, TestData.TenantId)))
            .AssertSuccess();

        IReadOnlyList<PlacementSummaryDto> deployments;
        switch (action)
        {
            case "redeploy":
                var redeployed = (await Mediator.Send(new RedeployWebServiceRequest(
                    created.WebServiceId, TestData.TenantId, ExpectedRevision: 2))).AssertSuccess();
                deployments = Assert.IsType<RedeployWebServiceResponse>(redeployed.Value).Deployments;
                break;
            case "rollback":
                deployments = (await Mediator.Send(new RollbackWebServiceRequest(
                    created.WebServiceId, new RollbackWebServiceRequestBody(1, 2), TestData.TenantId)))
                    .AssertSuccess().Deployments;
                break;
            default:
                var context = new TenantContextAccessor
                {
                    Current = new TenantContext(TestData.TenantId, "Test", TenantRole.Member, false)
                };
                var handler = ActivatorUtilities.CreateInstance<UpdateWebServiceImageTagHandler>(
                    Services, context);
                var updated = await handler.HandleAsync(new UpdateWebServiceImageTagRequest(
                    created.WebServiceId,
                    new UpdateWebServiceImageTagRequestBody("1.28", "manual"),
                    TestData.TenantId));
                deployments = Assert.IsType<UpdateWebServiceImageTagResponse>(updated.Value).Deployments;
                break;
        }

        Assert.Single(deployments);
        Assert.DoesNotContain(deployments, item => item.DeploymentId == removed.DeploymentId);
        await AssertUsageAsync(500, 512, 2, 1);
        var retained = await DbContext.ResourceDeployments
            .Find(item => item.Id == removed.DeploymentId).SingleAsync();
        Assert.Equal(ResourceDeploymentOperation.Delete, retained.DesiredOperation);
        var release = await Fixture.Database.WebServiceReleases()
            .Find(item => item.WebServiceId == created.WebServiceId && item.ResourceRevision == 3)
            .SingleAsync();
        Assert.Single(release.Deployments);
    }

    [Fact]
    public async Task ConcurrentTargetScaling_CannotOvercommitOrLeaveActiveReservations()
    {
        var created = await CreateMultiTargetAsync();
        await Fixture.Database.GetCollection<TenantQuotaPolicy>("TenantQuotaPolicies")
            .InsertOneAsync(QuotaDomainFactory.CreatePolicy(
                TestData.TenantId,
                QuotaDelta.From(CpuQuota.QuotaKey, 1750),
                DateTime.UtcNow));
        var load = Services.GetRequiredService<LoadResourceDeploymentSet>();
        var bothSnapshotsLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readCount = 0;
        LoadResourceDeploymentSet synchronizedLoad = async (request, token) =>
        {
            var snapshot = await load(
                new LoadResourceDeploymentSetRequest(
                    TenantId: request.TenantId,
                    ResourceType: request.ResourceType,
                    ResourceId: request.ResourceId),
                cancellationToken: token);
            if (Interlocked.Increment(ref readCount) == 2)
                bothSnapshotsLoaded.SetResult();
            await bothSnapshotsLoaded.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            return snapshot;
        };
        using var firstScope = Services.CreateScope();
        using var secondScope = Services.CreateScope();
        var first = ActivatorUtilities.CreateInstance<UpdateWebServiceDeploymentHandler>(
            firstScope.ServiceProvider, synchronizedLoad);
        var second = ActivatorUtilities.CreateInstance<UpdateWebServiceDeploymentHandler>(
            secondScope.ServiceProvider, synchronizedLoad);

        var results = await Task.WhenAll(
            first.HandleAsync(new UpdateWebServiceDeploymentRequest(
                created.WebServiceId, created.Deployments[0].DeploymentId,
                new UpdateWebServiceDeploymentBody(created.Deployments[0].FixedReplicas + 2),
                TestData.TenantId)),
            second.HandleAsync(new UpdateWebServiceDeploymentRequest(
                created.WebServiceId, created.Deployments[1].DeploymentId,
                new UpdateWebServiceDeploymentBody(created.Deployments[1].FixedReplicas + 2),
                TestData.TenantId)));

        Assert.Single(results, result => result.Value is PlacementSummaryDto);
        Assert.Single(results, result => result.Value is UpdateWebServiceDeploymentError);
        await AssertUsageAsync(1750, 1792, 7, 1);
        Assert.Equal(0, await Fixture.Database.GetCollection<QuotaReservation>("QuotaReservations")
            .CountDocumentsAsync(item => item.TenantId == TestData.TenantId &&
                item.State.Kind == QuotaReservationStateKinds.Active));
        var persisted = await DbContext.ResourceDeployments
            .Find(item => item.ResourceId == created.WebServiceId).ToListAsync();
        Assert.Equal(7, persisted.Sum(item => item.FixedReplicas));
    }

    [Fact]
    public async Task SharedScaling_StillChargesFailedDesiredPlacement()
    {
        var created = await CreateMultiTargetAsync();
        var original = await DbContext.ResourceDeployments
            .Find(item => item.Id == created.Deployments[0].DeploymentId).SingleAsync();
        var claimId = ObjectId.GenerateNewId().ToString();
        var started = original.UpdatedAtUtc.AddMilliseconds(1);
        var failed = original.MarkRuntimeCellReady(started)
            .Start(original.DesiredGeneration, ResourceDeploymentOperation.Apply,
                claimId, started, started.AddMinutes(1))
            .Fail(original.DesiredGeneration, ResourceDeploymentOperation.Apply,
                claimId, "TargetUnavailable", "Test target failure", false,
                started.AddMilliseconds(1), started.AddSeconds(1));
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == failed.Id, failed);
        var scaling = TestData.ValidWebServiceSpec() with
        {
            Autoscaling = new AutoscalingSpecDto(1, 4, new AutoscalingMetricDto("cpu", 80))
        };

        var updated = (await Mediator.Send(new UpdateWebServiceRequest(
            created.WebServiceId,
            new UpdateWebServiceRequestBody(scaling, [], WebServiceExposureMode.Public),
            TestData.TenantId))).AssertSuccess();

        Assert.Equal(2, updated.Deployments.Count);
        await AssertUsageAsync(2000, 2048, 8, 1);
    }

    private async Task<CreateWebServiceResponse> CreateMultiTargetAsync()
    {
        var secondTargetId = ObjectId.GenerateNewId().ToString();
        Services.GetRequiredService<TestDeploymentTargetLookup>()
            .AddEligibleTarget(secondTargetId, "apps.second.test.local");
        await Fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles")
            .ReplaceOneAsync(
                profile => profile.Id == TestData.DefaultComputeProfileId,
                TestData.SeedComputeProfile(deploymentTargetIds:
                    [TestData.DefaultDeploymentTargetId, secondTargetId]));
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var request = TestData.CreateWebServiceRequest(group.Id);
        return (await Mediator.Send(request with
        {
            Body = request.Body with
            {
                Placement = new WebServicePlacementRequest(null,
                [
                    new WebServiceDeploymentTargetRequest(TestData.DefaultDeploymentTargetId, 2),
                    new WebServiceDeploymentTargetRequest(secondTargetId, 3)
                ])
            }
        })).AssertSuccess();
    }

    private async Task AssertUsageAsync(
        long cpu,
        long memory,
        long instances,
        long services)
    {
        var usage = await Fixture.Database.GetCollection<TenantQuotaUsage>("TenantQuotaUsages")
            .Find(item => item.TenantId == TestData.TenantId)
            .SingleAsync();
        var values = usage.Values.ToDictionary(item => item.Key, item => item.Value);
        Assert.Equal(cpu, values.GetValueOrDefault(CpuQuota.QuotaKey));
        Assert.Equal(memory, values.GetValueOrDefault(RamQuota.QuotaKey));
        Assert.Equal(instances, values.GetValueOrDefault(WorkloadInstancesQuota.QuotaKey));
        Assert.Equal(services, values.GetValueOrDefault(WebServiceCountQuota.QuotaKey));
    }
}
