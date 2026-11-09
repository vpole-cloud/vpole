using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.Declarations;
using Pole.Features.WebServices.Application.Deployments;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Endpoints.Deployments;
using Pole.Features.WebServices.Application.Endpoints.Deployments.Hostnames;
using Pole.Features.WebServices.Application.Endpoints.Releases;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Application.SourceDelivery;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

[Collection(IntegrationTestCollection.Global2)]
public sealed class WebServiceIntegrationTests(MongoReplicaSetFixture fixture)
    : IsolatedMediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Update_PrivateToPublic_AdmitsTargetsAndCreatesGeneratedHostname()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var template = Data.CreateWebServiceRequest(group.Id);
        var created = (await Mediator.Send(template with
        {
            Body = template.Body with { Exposure = WebServiceExposureMode.Private }
        })).AssertSuccess();
        Assert.Null(Assert.Single(created.Deployments).Ingress);

        var updated = (await Mediator.Send(new UpdateWebServiceRequest(
            created.WebServiceId,
            new UpdateWebServiceRequestBody(
                TestData.ValidWebServiceSpec(),
                [],
                WebServiceExposureMode.Public),
            Data.TenantId))).AssertSuccess();

        var service = await Fixture.Database.WebServices()
            .Find(item => item.Id == created.WebServiceId).SingleAsync();
        var deployment = Assert.Single(updated.Deployments);
        Assert.Equal(WebServiceExposureMode.Public, service.Exposure.Mode);
        Assert.Single(deployment.Ingress!.Hostnames);
        Assert.Single(await Fixture.Database.GetCollection<HostnameBinding>("HostnameBindings")
            .Find(binding => binding.ResourceId == created.WebServiceId).ToListAsync());
        var release = await Fixture.Database.WebServiceReleases()
            .Find(item => item.WebServiceId == created.WebServiceId && item.ResourceRevision == 2)
            .SingleAsync();
        Assert.Equal(WebServiceExposureMode.Public, release.Exposure.Mode);
    }

    [Fact]
    public async Task Update_PublicToPrivate_ReleasesHostnameOnlyAfterAppliedRevocation()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var template = Data.CreateWebServiceRequest(group.Id);
        var created = (await Mediator.Send(template with
        {
            Body = template.Body with { Exposure = WebServiceExposureMode.Public }
        })).AssertSuccess();

        var privateUpdate = (await Mediator.Send(new UpdateWebServiceRequest(
            created.WebServiceId,
            new UpdateWebServiceRequestBody(
                TestData.ValidWebServiceSpec(),
                [],
                WebServiceExposureMode.Private),
            Data.TenantId))).AssertSuccess();
        var deployment = Assert.Single(privateUpdate.Deployments);
        var hostnameBindings = Fixture.Database.GetCollection<HostnameBinding>("HostnameBindings");
        Assert.Null(deployment.Ingress);
        Assert.Single(await hostnameBindings
            .Find(binding => binding.ResourceId == created.WebServiceId).ToListAsync());

        var completion = await Services.GetRequiredService<CompleteWebServiceDeployment>()(
            Data.TenantId,
            created.WebServiceId,
            new WebServiceDeploymentCompletion(
                deployment.DeploymentId,
                privateUpdate.ResourceRevision,
                deployment.DesiredGeneration,
                WebServiceReleaseDeploymentOperation.Apply,
                Succeeded: true,
                Retryable: false,
                DateTime.UtcNow,
                FailureCode: null,
                FailureMessage: null,
                EventCore.Create(Data.TenantId)));

        Assert.IsType<WebServiceDeploymentCompletionRecorded>(completion.Value);
        Assert.Empty(await hostnameBindings
            .Find(binding => binding.ResourceId == created.WebServiceId).ToListAsync());
        var privateService = await Fixture.Database.WebServices()
            .Find(item => item.Id == created.WebServiceId).SingleAsync();
        Assert.Equal(WebServiceExposureMode.Private, privateService.Exposure.Mode);
    }

    [Fact]
    public async Task Update_RePublicBeforeOldPrivateCompletion_PreservesCurrentHostnameBinding()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var template = Data.CreateWebServiceRequest(group.Id);
        var created = (await Mediator.Send(template with
        {
            Body = template.Body with { Exposure = WebServiceExposureMode.Public }
        })).AssertSuccess();

        var privateUpdate = (await Mediator.Send(new UpdateWebServiceRequest(
            created.WebServiceId,
            new UpdateWebServiceRequestBody(
                TestData.ValidWebServiceSpec(), [], WebServiceExposureMode.Private),
            Data.TenantId))).AssertSuccess();
        var stalePrivateDeployment = Assert.Single(privateUpdate.Deployments);
        var publicUpdate = (await Mediator.Send(new UpdateWebServiceRequest(
            created.WebServiceId,
            new UpdateWebServiceRequestBody(
                TestData.ValidWebServiceSpec(), [], WebServiceExposureMode.Public),
            Data.TenantId))).AssertSuccess();
        var publicDeployment = Assert.Single(publicUpdate.Deployments);

        var completion = await Services.GetRequiredService<CompleteWebServiceDeployment>()(
            Data.TenantId,
            created.WebServiceId,
            new WebServiceDeploymentCompletion(
                stalePrivateDeployment.DeploymentId,
                privateUpdate.ResourceRevision,
                stalePrivateDeployment.DesiredGeneration,
                WebServiceReleaseDeploymentOperation.Apply,
                Succeeded: true,
                Retryable: false,
                DateTime.UtcNow,
                FailureCode: null,
                FailureMessage: null,
                EventCore.Create(Data.TenantId)));

        Assert.IsType<WebServiceDeploymentCompletionRecorded>(completion.Value);
        Assert.NotNull(publicDeployment.Ingress);
        Assert.Single(await Fixture.Database.GetCollection<HostnameBinding>("HostnameBindings")
            .Find(binding => binding.ResourceId == created.WebServiceId).ToListAsync());
        var current = await Fixture.Database.WebServices()
            .Find(item => item.Id == created.WebServiceId).SingleAsync();
        Assert.Equal(WebServiceExposureMode.Public, current.Exposure.Mode);
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
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var created = (await Mediator.Send(Data.CreateWebServiceRequest(group.Id)))
            .AssertSuccess();

        var result = await Mediator.Send(new AddWebServiceDeploymentRequest(
            created.WebServiceId,
            new WebServiceDeploymentTargetRequest(secondTargetId, FixedReplicas: 3),
            Data.TenantId));
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
    public async Task AddDeployment_PrivateDoesNotRequirePublicIngressSource()
    {
        var secondTargetId = ObjectId.GenerateNewId().ToString();
        Services.GetRequiredService<TestDeploymentTargetLookup>()
            .AddIneligiblePublicIngressTarget(secondTargetId, "apps.private-only.test.local");
        await Fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles")
            .ReplaceOneAsync(
                profile => profile.Id == TestData.DefaultComputeProfileId,
                TestData.SeedComputeProfile(deploymentTargetIds:
                    [TestData.DefaultDeploymentTargetId, secondTargetId]));
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var template = Data.CreateWebServiceRequest(group.Id);
        var created = (await Mediator.Send(template with
        {
            Body = template.Body with { Exposure = WebServiceExposureMode.Private }
        })).AssertSuccess();

        var result = await Mediator.Send(new AddWebServiceDeploymentRequest(
            created.WebServiceId,
            new WebServiceDeploymentTargetRequest(secondTargetId, FixedReplicas: 2),
            Data.TenantId));
        var added = Assert.IsType<PlacementSummaryDto>(result.AssertSuccess().Value);

        Assert.Equal(secondTargetId, added.DeploymentTargetId);
        Assert.Null(added.Ingress);
        Assert.Empty(await Fixture.Database.GetCollection<HostnameBinding>("HostnameBindings")
            .Find(binding => binding.ResourceDeploymentId == added.DeploymentId).ToListAsync());
    }

    [Fact]
    public async Task AddCustomHostname_PrivateServiceIsRejected()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var template = Data.CreateWebServiceRequest(group.Id);
        var created = (await Mediator.Send(template with
        {
            Body = template.Body with { Exposure = WebServiceExposureMode.Private }
        })).AssertSuccess();
        var deployment = Assert.Single(created.Deployments);

        var result = await Mediator.Send(new AddWebServiceDeploymentHostnameRequest(
            created.WebServiceId,
            deployment.DeploymentId,
            new AddWebServiceDeploymentHostnameBody(
                ObjectId.GenerateNewId().ToString(),
                "custom",
                "custom-hostname"),
            Data.TenantId));
        var outcome = result.AssertSuccess();

        var error = Assert.IsType<WebServiceDeploymentHostnameError>(outcome.Value);
        Assert.IsType<WebServiceHostnameAttachmentUnavailable>(error.Value);
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
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var created = (await Mediator.Send(Data.CreateWebServiceRequest(group.Id)))
            .AssertSuccess();
        targetLookup.RejectNewWorkloads = true;

        var result = await Mediator.Send(new AddWebServiceDeploymentRequest(
            created.WebServiceId,
            new WebServiceDeploymentTargetRequest(secondTargetId, FixedReplicas: 3),
            Data.TenantId));

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
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var created = (await Mediator.Send(Data.CreateWebServiceRequest(group.Id))).AssertSuccess();
        var spec = TestData.ValidWebServiceSpec() with
        {
            Image = new Pole.Features.WebServices.Application.Common.ImageInfoDto("docker.io", "nginx", "1.28")
        };

        var result = await Mediator.Send(new UpdateWebServiceRequest(created.WebServiceId,
            new UpdateWebServiceRequestBody(spec, ["stable"], WebServiceExposureMode.Public), Data.TenantId));
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
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var created = (await Mediator.Send(Data.CreateWebServiceRequest(group.Id))).AssertSuccess();
        var deployment = created.Deployments.Single();
        var result = await Mediator.Send(new DeleteWebServiceDeploymentRequest(
            created.WebServiceId, deployment.DeploymentId, Data.TenantId));
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
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var request = Data.CreateWebServiceRequest(group.Id) with
        {
            Body = Data.CreateWebServiceRequest(group.Id).Body with
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
            created.WebServiceId, removed.DeploymentId, Data.TenantId));
        var deleting = result.AssertSuccess();

        Assert.Equal(removed.DeploymentId, deleting.DeploymentId);
        Assert.Equal(ResourceDeploymentStatus.Deleting, deleting.Status);
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
            Data.TenantId,
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
                EventCore.Create(Data.TenantId)))).AssertSuccess();

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
            Data.SeedResourceGroup());
        var request = Data.CreateWebServiceRequest(group.Id) with
        {
            Body = Data.CreateWebServiceRequest(group.Id).Body with
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
            Data.TenantId))).AssertSuccess();

        var secondRemoval = await Mediator.Send(
            new DeleteWebServiceDeploymentRequest(
                created.WebServiceId,
                created.Deployments[1].DeploymentId,
                Data.TenantId));

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
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var created = (await Mediator.Send(Data.CreateWebServiceRequest(group.Id)))
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
            Data.TenantId));
        var retried = result.AssertSuccess();

        Assert.Equal(2, retried.ResourceRevision);
        Assert.Equal(ResourceDeploymentStatus.Pending, retried.Status);
        var release = await Fixture.Database.WebServiceReleases()
            .Find(item => item.WebServiceId == created.WebServiceId &&
                          item.ResourceRevision == 2)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseAction.RetryDeployment, release.Action);
        var entry = Assert.Single(release.Deployments);
        Assert.Equal(WebServiceReleaseDeploymentOutcome.Pending, entry.Outcome);

        var completed = await Services.GetRequiredService<CompleteWebServiceDeployment>()(
            Data.TenantId,
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
                EventCore.Create(Data.TenantId)));
        completed.AssertSuccess();

        release = await Fixture.Database.WebServiceReleases()
            .Find(item => item.Id == release.Id)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseOutcome.Succeeded, release.Outcome);
    }

    [Fact]
    public async Task UpdateDeployment_OmissionPreservesCompute_AndExplicitIdAdoptsLatestRevision()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var created = (await Mediator.Send(Data.CreateWebServiceRequest(group.Id)))
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
            Data.TenantId))).AssertSuccess();
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
            Data.TenantId))).AssertSuccess();
        var adopted = await DbContext.ResourceDeployments
            .Find(item => item.Id == initial.DeploymentId).SingleAsync();
        Assert.Equal(2, adopted.ResolvedPlacement.Compute.ComputeProfileRevision);
        Assert.Equal(500, adopted.ResolvedPlacement.Compute.Requests.CpuMillis);
    }

    [Fact]
    public async Task Delete_RetainsBindingsUntilTerminalDeployment_ThenFinalizesEverything()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var created = (await Mediator.Send(Data.CreateWebServiceRequest(group.Id)))
            .AssertSuccess();
        var deploymentId = Assert.Single(created.Deployments).DeploymentId;

        (await Mediator.Send(new DeleteWebServiceRequest(
            created.WebServiceId, Data.TenantId))).AssertSuccess();
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
                Data.TenantId,
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
        Assert.Equal(1L, await DbContext.CountAllOutboxMessagesAsync("pole.web-services/v1/web-service-deleted"));
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
