using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

public sealed class WebServiceMetadataUpdateTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private const string ApplyRequested = "pole.resource-deployments/v1/resource-deployment-reconciliation-requested";
    private const string Updated = "pole.web-services/v1/web-service-updated";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_after_reconciliation_claim_preserves_metadata_but_fences_physical_changes(bool configurationChanged)
    {
        var service = await CreatePrivateServiceAsync();
        var initial = await LoadDeploymentAsync(service.Id);
        var pending = initial.MarkRuntimeCellReady(initial.UpdatedAtUtc);
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == pending.Id, pending);
        var requestedBefore = await DbContext.CountOutboxMessagesAsync(ApplyRequested);
        var load = Services.GetRequiredService<LoadResourceDeploymentSet>();
        ResourceDeployment? claimed = null;
        LoadResourceDeploymentSet interleavedLoad = async (request, ct) =>
        {
            var snapshot = await load(
                new LoadResourceDeploymentSetRequest(
                    TenantId: request.TenantId,
                    ResourceType: request.ResourceType,
                    ResourceId: request.ResourceId),
                cancellationToken: ct);
            var lease = await Services.GetRequiredService<ClaimResourceDeployment>()(
                request.TenantId, pending.Id, pending.RuntimeCellId, pending.DesiredGeneration,
                ResourceDeploymentOperation.Apply, ct);
            Assert.NotNull(lease);
            claimed = await LoadDeploymentAsync(service.Id, ct);
            return snapshot;
        };
        var handler = ActivatorUtilities.CreateInstance<UpdateWebServiceHandler>(Services, interleavedLoad);
        var spec = service.Spec.ToDto();
        if (configurationChanged)
            spec = ChangeEnvironment(spec);

        var result = await handler.HandleAsync(new UpdateWebServiceRequest(
            service.Id,
            new UpdateWebServiceRequestBody(
                spec, ["metadata"], service.Exposure.Mode, service.ActiveRevision),
            service.TenantId));

        if (configurationChanged)
        {
            var error = Assert.IsType<UpdateWebServiceError>(result.Value);
            var deploymentError = Assert.IsType<ResourceDeploymentMutationError>(error.Value);
            Assert.IsType<ResourceDeploymentChanged>(deploymentError.Value);
        }
        else
        {
            if (result.Value is UpdateWebServiceError metadataError)
            {
                var conflict = Assert.IsType<ResourceDeploymentMutationError>(metadataError.Value);
                Assert.IsType<ResourceDeploymentChanged>(conflict.Value);
                Assert.Fail("Metadata-only update returned resource-deployments.changed after a reconciliation claim.");
            }
            var accepted = Assert.IsType<UpdateWebServiceResponse>(result.Value);
            Assert.Equal(service.ActiveRevision + 1, accepted.ResourceRevision);
        }
        var saved = await LoadDeploymentAsync(service.Id);
        var activeClaim = Assert.IsType<ResourceDeployment>(claimed);
        Assert.Equal(pending.DesiredGeneration, saved.DesiredGeneration);
        Assert.Equal(activeClaim.State, saved.State);
        Assert.IsType<ReconcilingResourceDeploymentState>(saved.State);
        Assert.Equal(configurationChanged ? service.ActiveRevision : service.ActiveRevision + 1, saved.ResourceRevision);
        Assert.Equal(requestedBefore, await DbContext.CountOutboxMessagesAsync(ApplyRequested));
        Assert.Equal(configurationChanged ? 0 : 1, await DbContext.CountOutboxMessagesAsync(Updated));
        var stored = await Fixture.Database.WebServices()
            .Find(item => item.Id == service.Id).SingleAsync();
        Assert.Equal(saved.ResourceRevision, stored.ActiveRevision);
        Assert.True(stored.Tags.SetEquals(configurationChanged ? service.Tags : ["metadata"]));
        Assert.Equal(configurationChanged ? 1 : 2, await Fixture.Database.WebServiceReleases()
            .CountDocumentsAsync(item => item.WebServiceId == service.Id));

        await Services.GetRequiredService<PersistResourceDeploymentSuccess>()(
            activeClaim, pending.DesiredGeneration, ResourceDeploymentOperation.Apply, []);
        var completed = await LoadDeploymentAsync(service.Id);
        Assert.IsType<AppliedResourceDeploymentState>(completed.State);
        Assert.Equal(saved.ResourceRevision, completed.ResourceRevision);
        Assert.Equal(saved.DesiredGeneration, completed.DesiredGeneration);
    }

    [Fact]
    public async Task Equivalent_HTTP_configuration_is_a_no_op_without_releases_or_outbox_writes()
    {
        var service = await CreatePrivateServiceAsync();
        var before = await DbContext.OutboxMessages.CountDocumentsAsync(_ => true);
        var placement = await LoadDeploymentAsync(service.Id);

        var accepted = (await Mediator.Send(new UpdateWebServiceRequest(
            service.Id,
            new UpdateWebServiceRequestBody(
                service.Spec.ToDto(), service.Tags, service.Exposure.Mode, service.ActiveRevision),
            service.TenantId))).AssertSuccess();

        Assert.Equal(service.ActiveRevision, accepted.ResourceRevision);
        var stored = await Fixture.Database.WebServices()
            .Find(item => item.Id == service.Id).SingleAsync();
        Assert.Equal(service.Revision, stored.Revision);
        Assert.Equal(placement.Revision, (await LoadDeploymentAsync(service.Id)).Revision);
        Assert.Equal(before, await DbContext.OutboxMessages.CountDocumentsAsync(_ => true));
        Assert.Equal(1, await Fixture.Database.WebServiceReleases()
            .CountDocumentsAsync(item => item.WebServiceId == service.Id));
    }

    [Fact]
    public async Task Changed_configuration_still_requests_a_new_physical_generation()
    {
        var service = await CreatePrivateServiceAsync();
        var placement = await LoadDeploymentAsync(service.Id);
        var spec = ChangeEnvironment(service.Spec.ToDto());

        var accepted = (await Mediator.Send(new UpdateWebServiceRequest(
            service.Id,
            new UpdateWebServiceRequestBody(
                spec, service.Tags, service.Exposure.Mode, service.ActiveRevision),
            service.TenantId))).AssertSuccess();

        Assert.Equal(service.ActiveRevision + 1, accepted.ResourceRevision);
        Assert.Equal(placement.DesiredGeneration + 1, Assert.Single(accepted.Deployments).DesiredGeneration);
        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync(Updated));
    }

    private async Task<WebService> CreatePrivateServiceAsync()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var request = TestData.CreateWebServiceRequest(group.Id);
        var created = (await Mediator.Send(request with
        {
            Body = request.Body with { Exposure = WebServiceExposureMode.Private }
        })).AssertSuccess();
        return await Fixture.Database.WebServices()
            .Find(item => item.Id == created.WebServiceId).SingleAsync();
    }

    private Task<ResourceDeployment> LoadDeploymentAsync(string serviceId, CancellationToken ct = default) =>
        DbContext.ResourceDeployments.Find(item => item.ResourceId == serviceId).SingleAsync(ct);

    private static WebServiceSpecDto ChangeEnvironment(WebServiceSpecDto spec) => spec with
    {
        Config = spec.Config with
        {
            EnvironmentVariables = spec.Config.EnvironmentVariables.SetItem("MODE", "changed")
        }
    };
}
