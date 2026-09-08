using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Features.Events.Abstractions;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Endpoints.Deployments;
using Pole.Features.WebServices.Application.Endpoints.Releases;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Application.Persistence.Deployments;
using Pole.Features.WebServices.Application.Persistence.Lifecycle;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

public sealed class WebServiceIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Create_PersistsGeneratedHostnameAndMultiTargetPlacementSnapshot()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var result = await Mediator.Send(TestData.CreateWebServiceRequest(group.Id));
        var created = result.AssertSuccess();

        var service = await Fixture.Database.WebServices()
            .Find(item => item.Id == created.WebServiceId).SingleAsync();
        Assert.Equal(group.DefaultIsolationScopeId, service.IsolationScopeId);
        Assert.Single(created.Deployments);
        Assert.Equal(ResourcePlacementCapability.MultiTarget,
            (await DbContext.ResourceDeployments.Find(item => item.ResourceId == service.Id).SingleAsync())
            .PlacementCapability);

        var deployment = created.Deployments.Single();
        Assert.Equal(2, deployment.FixedReplicas);
        Assert.Single(deployment.Ingress!.Hostnames);
        Assert.StartsWith("webservice-", deployment.Ingress.Hostnames.Single());
        Assert.Equal(1, await Fixture.Database.GetCollection<HostnameBinding>("HostnameBindings")
            .CountDocumentsAsync(binding => binding.ResourceDeploymentId == deployment.DeploymentId));
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("pole.web-services/v1/web-service-created"));
        var registration = await DbContext.ResourceRegistrations
            .Find(item => item.ResourceId == service.Id)
            .SingleAsync();
        Assert.Equal(WebServiceResourceDeployment.ResourceType.Value, registration.ResourceTypeId);
        Assert.Equal(group.Id, registration.ResourceGroupId);
        Assert.Equal(service.Name, registration.DisplayName);
    }

    [Fact]
    public async Task AddDeployment_PersistsTargetBindingAndAggregateRelease()
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
        var created = (await Mediator.Send(TestData.CreateWebServiceRequest(group.Id)))
            .AssertSuccess();

        var result = await Mediator.Send(new AddWebServiceDeploymentRequest(
            created.WebServiceId,
            new WebServiceDeploymentTargetRequest(secondTargetId, FixedReplicas: 3),
            TestData.TenantId));
        var added = Assert.IsType<PlacementSummaryDto>(
            result.AssertSuccess().Value);

        Assert.Equal(secondTargetId, added.DeploymentTargetId);
        Assert.Equal(3, added.FixedReplicas);
        Assert.Equal(2, added.ResourceRevision);
        Assert.Equal(2, await DbContext.ResourceDeployments.CountDocumentsAsync(
            deployment => deployment.ResourceId == created.WebServiceId));
        Assert.Equal(1, await Fixture.Database
            .GetCollection<HostnameBinding>("HostnameBindings")
            .CountDocumentsAsync(binding =>
                binding.ResourceDeploymentId == added.DeploymentId));
        var release = await Fixture.Database.WebServiceReleases()
            .Find(item =>
                item.WebServiceId == created.WebServiceId &&
                item.ResourceRevision == 2)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseAction.AddDeployment, release.Action);
        Assert.Equal(2, release.Deployments.Count);
    }

    [Fact]
    public async Task AddDeployment_MapsTargetAdmissionRejectionWithoutPersistingMutation()
    {
        var secondTargetId = ObjectId.GenerateNewId().ToString();
        var targetLookup = Services.GetRequiredService<TestDeploymentTargetLookup>();
        targetLookup.AddEligibleTarget(secondTargetId, "apps.second.test.local");
        await Fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles")
            .ReplaceOneAsync(
                profile => profile.Id == TestData.DefaultComputeProfileId,
                TestData.SeedComputeProfile(deploymentTargetIds:
                    [TestData.DefaultDeploymentTargetId, secondTargetId]));
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = (await Mediator.Send(TestData.CreateWebServiceRequest(group.Id)))
            .AssertSuccess();
        targetLookup.RejectNewWorkloads = true;

        var result = await Mediator.Send(new AddWebServiceDeploymentRequest(
            created.WebServiceId,
            new WebServiceDeploymentTargetRequest(secondTargetId, FixedReplicas: 3),
            TestData.TenantId));

        var outcome = result.AssertSuccess();
        var addError = Assert.IsType<AddWebServiceDeploymentError>(outcome.Value);
        var targetError = Assert.IsType<DeploymentTargetPublicIngressAdmissionError>(
            addError.Value);
        var unavailable = Assert.IsType<DeploymentTargetNewWorkloadsDisabled>(
            targetError.Value);
        Assert.Equal(secondTargetId, unavailable.DeploymentTargetId);
        Assert.Single(await DbContext.ResourceDeployments
            .Find(deployment => deployment.ResourceId == created.WebServiceId)
            .ToListAsync());
        Assert.Single(await Fixture.Database.WebServiceReleases()
            .Find(release => release.WebServiceId == created.WebServiceId)
            .ToListAsync());
    }

    [Fact]
    public async Task SharedUpdate_AdvancesEveryDeploymentAndCreatesAggregateRelease()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = (await Mediator.Send(TestData.CreateWebServiceRequest(group.Id))).AssertSuccess();
        var spec = TestData.ValidWebServiceSpec() with
        {
            Image = new Pole.Features.WebServices.Application.Common.ImageInfoDto("docker.io", "nginx", "1.28")
        };

        var result = await Mediator.Send(new UpdateWebServiceRequest(created.WebServiceId,
            new UpdateWebServiceRequestBody(spec, ["stable"]), TestData.TenantId));
        var updated = result.AssertSuccess();
        Assert.Equal(2, updated.ResourceRevision);
        Assert.All(updated.Deployments, deployment => Assert.Equal(2, deployment.ResourceRevision));
        var release = await Fixture.Database.WebServiceReleases()
            .Find(item => item.WebServiceId == created.WebServiceId && item.ResourceRevision == 2)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseAction.Update, release.Action);
        Assert.Equal(updated.Deployments.Count, release.Deployments.Count);
    }

    [Fact]
    public async Task RemoveDeployment_RejectsLastDesiredDeployment()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = (await Mediator.Send(TestData.CreateWebServiceRequest(group.Id))).AssertSuccess();
        var deployment = created.Deployments.Single();
        var result = await Mediator.Send(new DeleteWebServiceDeploymentRequest(
            created.WebServiceId, deployment.DeploymentId, TestData.TenantId));
        var error = result.AssertFailure();
        var state = Assert.IsType<DeleteWebServiceDeploymentStateError>(error.Value);
        Assert.IsType<WebServiceLastDeploymentCannotBeRemoved>(state.Value);
    }

    [Fact]
    public async Task RemoveDeployment_CreatesAggregateDeleteRelease_AndKeepsSibling()
    {
        var secondTargetId = MongoDB.Bson.ObjectId.GenerateNewId().ToString();
        Services.GetRequiredService<TestDeploymentTargetLookup>()
            .AddEligibleTarget(secondTargetId, "apps.second.test.local");
        await Fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles")
            .ReplaceOneAsync(
                profile => profile.Id == TestData.DefaultComputeProfileId,
                TestData.SeedComputeProfile(deploymentTargetIds:
                    [TestData.DefaultDeploymentTargetId, secondTargetId]));
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var request = TestData.CreateWebServiceRequest(group.Id) with
        {
            Body = TestData.CreateWebServiceRequest(group.Id).Body with
            {
                Placement = new WebServicePlacementRequest(null,
                [
                    new WebServiceDeploymentTargetRequest(
                        TestData.DefaultDeploymentTargetId, 2),
                    new WebServiceDeploymentTargetRequest(secondTargetId, 1)
                ])
            }
        };
        var created = (await Mediator.Send(request)).AssertSuccess();
        var removed = created.Deployments.Single(deployment =>
            deployment.DeploymentTargetId == secondTargetId);

        var result = await Mediator.Send(new DeleteWebServiceDeploymentRequest(
            created.WebServiceId, removed.DeploymentId, TestData.TenantId));
        var deleting = result.AssertSuccess();

        Assert.Equal(removed.DeploymentId, deleting.DeploymentId);
        Assert.Equal("Deleting", deleting.Status);
        var release = await Fixture.Database.WebServiceReleases()
            .Find(item => item.WebServiceId == created.WebServiceId &&
                          item.ResourceRevision == 2)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseAction.RemoveDeployment, release.Action);
        Assert.Equal(2, release.Deployments.Count);
        var removedEntry = Assert.Single(release.Deployments, entry =>
            entry.ResourceDeploymentId == removed.DeploymentId);
        Assert.Equal(WebServiceReleaseDeploymentOperation.Delete, removedEntry.Operation);
        Assert.Equal(WebServiceReleaseDeploymentOutcome.Pending, removedEntry.Outcome);
        Assert.Contains(release.Deployments, entry =>
            entry.ResourceDeploymentId != removed.DeploymentId &&
            entry.Operation == WebServiceReleaseDeploymentOperation.Apply);
        Assert.Equal(2, await DbContext.ResourceDeployments.CountDocumentsAsync(
            deployment => deployment.ResourceId == created.WebServiceId));

        var deletingDeployment = await DbContext.ResourceDeployments
            .Find(deployment => deployment.Id == removed.DeploymentId)
            .SingleAsync();
        var claimId = ObjectId.GenerateNewId().ToString();
        var deletedAtUtc = deletingDeployment.UpdatedAtUtc;
        var terminal = deletingDeployment
            .Start(deletingDeployment.DesiredGeneration,
                ResourceDeploymentOperation.Delete, claimId, deletedAtUtc,
                deletedAtUtc.AddMinutes(1))
            .Complete(deletingDeployment.DesiredGeneration,
                ResourceDeploymentOperation.Delete, claimId, [], deletedAtUtc);
        await DbContext.ResourceDeployments.ReplaceOneAsync(
            deployment => deployment.Id == removed.DeploymentId, terminal);
        (await Services.GetRequiredService<CompleteWebServiceDeployment>()(
            TestData.TenantId,
            created.WebServiceId,
            new WebServiceDeploymentCompletion(
                removed.DeploymentId,
                deleting.ResourceRevision,
                deleting.DesiredGeneration,
                WebServiceReleaseDeploymentOperation.Delete,
                Succeeded: true,
                Retryable: false,
                DateTime.UtcNow,
                null,
                null,
                EventCore.Create(TestData.TenantId)))).AssertSuccess();

        Assert.Equal(1, await DbContext.ResourceDeployments.CountDocumentsAsync(
            deployment => deployment.ResourceId == created.WebServiceId));
        Assert.Equal(0, await Fixture.Database
            .GetCollection<HostnameBinding>("HostnameBindings")
            .CountDocumentsAsync(binding =>
                binding.ResourceDeploymentId == removed.DeploymentId));
    }

    [Fact]
    public async Task RemoveDeployment_RejectsRemovingLastDesiredDeploymentWhileSiblingIsDeleting()
    {
        var secondTargetId = ObjectId.GenerateNewId().ToString();
        Services.GetRequiredService<TestDeploymentTargetLookup>()
            .AddEligibleTarget(secondTargetId, "apps.second.test.local");
        await Fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles")
            .ReplaceOneAsync(
                profile => profile.Id == TestData.DefaultComputeProfileId,
                TestData.SeedComputeProfile(deploymentTargetIds:
                    [TestData.DefaultDeploymentTargetId, secondTargetId]));
        var group = await DbContext.InsertResourceGroupAsync(
            TestData.SeedResourceGroup());
        var request = TestData.CreateWebServiceRequest(group.Id) with
        {
            Body = TestData.CreateWebServiceRequest(group.Id).Body with
            {
                Placement = new WebServicePlacementRequest(
                    null,
                    [
                        new WebServiceDeploymentTargetRequest(
                            TestData.DefaultDeploymentTargetId,
                            2),
                        new WebServiceDeploymentTargetRequest(
                            secondTargetId,
                            1)
                    ])
            }
        };
        var created = (await Mediator.Send(request)).AssertSuccess();

        _ = (await Mediator.Send(new DeleteWebServiceDeploymentRequest(
            created.WebServiceId,
            created.Deployments[0].DeploymentId,
            TestData.TenantId))).AssertSuccess();

        var secondRemoval = await Mediator.Send(
            new DeleteWebServiceDeploymentRequest(
                created.WebServiceId,
                created.Deployments[1].DeploymentId,
                TestData.TenantId));

        var error = secondRemoval.AssertFailure();
        var state = Assert.IsType<DeleteWebServiceDeploymentStateError>(error.Value);
        Assert.IsType<WebServiceLastDeploymentCannotBeRemoved>(state.Value);
        var retained = await DbContext.ResourceDeployments
            .Find(deployment =>
                deployment.Id == created.Deployments[1].DeploymentId)
            .SingleAsync();
        Assert.Equal(ResourceDeploymentOperation.Apply, retained.DesiredOperation);
    }

    [Fact]
    public async Task RetryDeployment_CreatesNewRelease_ThatCanCompleteSuccessfully()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = (await Mediator.Send(TestData.CreateWebServiceRequest(group.Id)))
            .AssertSuccess();
        var deployment = await DbContext.ResourceDeployments
            .Find(item => item.Id == created.Deployments.Single().DeploymentId)
            .SingleAsync();
        var startedAtUtc = deployment.UpdatedAtUtc;
        var claimId = ObjectId.GenerateNewId().ToString();
        var ready = deployment.MarkRuntimeCellReady(deployment.UpdatedAtUtc);
        var failed = ready
            .Start(deployment.DesiredGeneration, ResourceDeploymentOperation.Apply,
                claimId, startedAtUtc, startedAtUtc.AddMinutes(1))
            .Fail(deployment.DesiredGeneration, ResourceDeploymentOperation.Apply,
                claimId, "Transient", "Retry this deployment.", retryable: true,
                startedAtUtc, startedAtUtc);
        await DbContext.ResourceDeployments.ReplaceOneAsync(
            item => item.Id == deployment.Id, failed);

        var result = await Mediator.Send(new RetryWebServiceDeploymentRequest(
            created.WebServiceId,
            deployment.Id,
            new RetryWebServiceDeploymentBody(failed.Revision, "Apply"),
            TestData.TenantId));
        var retried = result.AssertSuccess();

        Assert.Equal(2, retried.ResourceRevision);
        Assert.Equal("Pending", retried.Status);
        var release = await Fixture.Database.WebServiceReleases()
            .Find(item => item.WebServiceId == created.WebServiceId &&
                          item.ResourceRevision == 2)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseAction.RetryDeployment, release.Action);
        var entry = Assert.Single(release.Deployments);
        Assert.Equal(WebServiceReleaseDeploymentOutcome.Pending, entry.Outcome);

        var completed = await Services.GetRequiredService<CompleteWebServiceDeployment>()(
            TestData.TenantId,
            created.WebServiceId,
            new WebServiceDeploymentCompletion(
                deployment.Id,
                retried.ResourceRevision,
                retried.DesiredGeneration,
                WebServiceReleaseDeploymentOperation.Apply,
                Succeeded: true,
                Retryable: false,
                DateTime.UtcNow,
                null,
                null,
                EventCore.Create(TestData.TenantId)));
        completed.AssertSuccess();

        release = await Fixture.Database.WebServiceReleases()
            .Find(item => item.Id == release.Id)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseOutcome.Succeeded, release.Outcome);
    }

    [Fact]
    public async Task UpdateDeployment_OmissionPreservesCompute_AndExplicitIdAdoptsLatestRevision()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = (await Mediator.Send(TestData.CreateWebServiceRequest(group.Id)))
            .AssertSuccess();
        var initial = Assert.Single(created.Deployments);
        var profiles = Fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles");
        var currentProfile = await profiles.Find(profile =>
            profile.Id == TestData.DefaultComputeProfileId).SingleAsync();
        var revisedProfile = currentProfile with
        {
            Resources = ComputeProfileResourceRequirements.Create(
                ComputeProfileResourceQuantity.Create(500, 512),
                ComputeProfileResourceQuantity.Create(1500, 1536)),
            Revision = 2,
            UpdatedAtUtc = currentProfile.UpdatedAtUtc.AddMinutes(1)
        };
        await profiles.ReplaceOneAsync(profile => profile.Id == currentProfile.Id,
            revisedProfile);

        (await Mediator.Send(new UpdateWebServiceDeploymentRequest(
            created.WebServiceId,
            initial.DeploymentId,
            new UpdateWebServiceDeploymentBody(initial.FixedReplicas),
            TestData.TenantId))).AssertSuccess();
        var preserved = await DbContext.ResourceDeployments
            .Find(item => item.Id == initial.DeploymentId).SingleAsync();
        Assert.Equal(1, preserved.ResolvedPlacement.Compute.ComputeProfileRevision);
        Assert.Equal(250, preserved.ResolvedPlacement.Compute.Requests.CpuMillis);

        (await Mediator.Send(new UpdateWebServiceDeploymentRequest(
            created.WebServiceId,
            initial.DeploymentId,
            new UpdateWebServiceDeploymentBody(
                initial.FixedReplicas,
                TestData.DefaultComputeProfileId),
            TestData.TenantId))).AssertSuccess();
        var adopted = await DbContext.ResourceDeployments
            .Find(item => item.Id == initial.DeploymentId).SingleAsync();
        Assert.Equal(2, adopted.ResolvedPlacement.Compute.ComputeProfileRevision);
        Assert.Equal(500, adopted.ResolvedPlacement.Compute.Requests.CpuMillis);
    }

    [Fact]
    public async Task Rollback_RestoresSucceededReleaseSpec()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = (await Mediator.Send(TestData.CreateWebServiceRequest(group.Id)))
            .AssertSuccess();
        await Fixture.Database.WebServiceReleases().UpdateOneAsync(
            release => release.WebServiceId == created.WebServiceId &&
                       release.ResourceRevision == 1,
            Builders<WebServiceRelease>.Update
                .Set(release => release.Outcome, WebServiceReleaseOutcome.Succeeded)
                .Set(release => release.CompletedAtUtc, DateTime.UtcNow));
        var updatedSpec = TestData.ValidWebServiceSpec() with
        {
            Image = new ImageInfoDto("docker.io", "nginx", "1.28")
        };
        (await Mediator.Send(new UpdateWebServiceRequest(
            created.WebServiceId,
            new UpdateWebServiceRequestBody(updatedSpec, []),
            TestData.TenantId))).AssertSuccess();

        var rollback = (await Mediator.Send(new RollbackWebServiceRequest(
            created.WebServiceId,
            new RollbackWebServiceRequestBody(1),
            TestData.TenantId))).AssertSuccess();

        Assert.Equal(3, rollback.ResourceRevision);
        var saved = await Fixture.Database.WebServices()
            .Find(service => service.Id == created.WebServiceId).SingleAsync();
        Assert.Equal("1.27", saved.Spec.Image.Tag);
        var release = await Fixture.Database.WebServiceReleases()
            .Find(item => item.Id == rollback.ReleaseId).SingleAsync();
        Assert.Equal(WebServiceReleaseAction.Rollback, release.Action);
        Assert.Equal("1.27", release.Spec.Image.Tag);
    }

    [Fact]
    public async Task Rollback_DoesNotReadSucceededReleaseFromAnotherTenant()
    {
        const string otherTenantId = "507f1f77bcf86cd799439022";
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = (await Mediator.Send(TestData.CreateWebServiceRequest(group.Id)))
            .AssertSuccess();
        var placement = Assert.Single(created.Deployments);
        var foreign = WebServiceReleaseFactory.CreatePending(
            otherTenantId,
            created.WebServiceId,
            7,
            WebServiceReleaseAction.Update,
            null,
            null,
            TestData.ValidWebServiceSpec().ToDomain(),
            [new WebServiceReleaseDeployment(
                placement.DeploymentId,
                placement.DeploymentTargetId,
                7,
                placement.DesiredGeneration,
                WebServiceReleaseDeploymentOperation.Apply,
                WebServiceReleaseDeploymentOutcome.Succeeded,
                DateTime.UtcNow,
                null,
                null)],
            null,
            null,
            new WebServiceReleaseFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(), DateTime.UtcNow)) with
        {
            Outcome = WebServiceReleaseOutcome.Succeeded,
            CompletedAtUtc = DateTime.UtcNow
        };
        await Fixture.Database.WebServiceReleases().InsertOneAsync(foreign);

        var rollback = await Mediator.Send(new RollbackWebServiceRequest(
            created.WebServiceId,
            new RollbackWebServiceRequestBody(7),
            TestData.TenantId));
        var rollbackError = rollback.AssertFailure();
        var rollbackState = Assert.IsType<RollbackWebServiceStateError>(rollbackError.Value);
        Assert.IsType<WebServiceReleaseNotFound>(rollbackState.Value);
        var history = await Mediator.Send(new GetWebServiceReleasesRequest
        {
            Id = created.WebServiceId,
            TenantId = otherTenantId
        });
        Assert.IsType<WebServiceNotFound>(history.AssertFailure());
    }

    [Fact]
    public async Task Create_ReturnsNotFound_WhenResourceGroupDoesNotExist()
    {
        var missingId = ObjectId.GenerateNewId().ToString();
        var result = await Mediator.Send(TestData.CreateWebServiceRequest(missingId));
        var error = result.AssertFailure();
        var ownership = Assert.IsType<ResourceGroupOwnershipError>(error.Value);
        var notFound = Assert.IsType<ResourceGroupOwnershipNotFound>(ownership.Value);
        Assert.Equal(missingId, notFound.ResourceGroupId);
    }

    [Fact]
    public async Task GetList_FiltersBySearch_AndAppliesPagination()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        (await Mediator.Send(TestData.CreateWebServiceRequest(group.Id, "api-alpha")))
            .AssertSuccess();
        (await Mediator.Send(TestData.CreateWebServiceRequest(group.Id, "api-beta")))
            .AssertSuccess();

        var page = (await Mediator.Send(new GetWebServicesRequest
        {
            TenantId = TestData.TenantId,
            Search = "beta",
            PageNumber = 1,
            PageSize = 1
        })).AssertSuccess();
        var service = Assert.Single(page.Items);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal("api-beta", service.Name);
        Assert.Single(service.Deployments);
    }

    [Fact]
    public async Task Delete_RetainsBindingsUntilTerminalDeployment_ThenFinalizesEverything()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = (await Mediator.Send(TestData.CreateWebServiceRequest(group.Id)))
            .AssertSuccess();
        var deploymentId = Assert.Single(created.Deployments).DeploymentId;

        (await Mediator.Send(new DeleteWebServiceRequest(
            created.WebServiceId, TestData.TenantId))).AssertSuccess();
        Assert.Equal(1, await Fixture.Database
            .GetCollection<HostnameBinding>("HostnameBindings")
            .CountDocumentsAsync(binding => binding.ResourceDeploymentId == deploymentId));
        var deleting = await DbContext.ResourceDeployments
            .Find(item => item.Id == deploymentId).SingleAsync();
        var startedAtUtc = deleting.UpdatedAtUtc.AddSeconds(1);
        var claimId = ObjectId.GenerateNewId().ToString();
        var deleted = deleting
            .Start(deleting.DesiredGeneration, ResourceDeploymentOperation.Delete,
                claimId, startedAtUtc, startedAtUtc.AddMinutes(1))
            .Complete(deleting.DesiredGeneration, ResourceDeploymentOperation.Delete,
                claimId, [], startedAtUtc.AddSeconds(1));
        await DbContext.ResourceDeployments.ReplaceOneAsync(
            item => item.Id == deploymentId, deleted);

        (await Services.GetRequiredService<FinalizeWebServiceDeletion>()(
                TestData.TenantId,
                created.WebServiceId)).AssertSuccess();

        Assert.Equal(0, await Fixture.Database.WebServices()
            .CountDocumentsAsync(service => service.Id == created.WebServiceId));
        Assert.Equal(0, await DbContext.ResourceDeployments
            .CountDocumentsAsync(item => item.ResourceId == created.WebServiceId));
        Assert.Equal(0, await Fixture.Database
            .GetCollection<HostnameBinding>("HostnameBindings")
            .CountDocumentsAsync(binding => binding.ResourceId == created.WebServiceId));
        Assert.Equal(0, await DbContext.ResourceRegistrations
            .CountDocumentsAsync(item => item.ResourceId == created.WebServiceId));
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(
            "pole.web-services/v1/web-service-deleted"));
        var deletionMessage = await DbContext.OutboxMessages
            .Find(message => message.EventType == "pole.web-services/v1/web-service-deleted")
            .SingleAsync();
        var deletionEvent = JsonSerializer.Deserialize<WebServiceDeletedEvent>(
            deletionMessage.PayloadJson,
            Pole.Features.Events.Abstractions.EventExtensions.SerializerOptions);
        Assert.NotNull(deletionEvent);
        Assert.Equal(2, deletionEvent.ResourceRevision);
    }
}
