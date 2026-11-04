using MongoDB.Driver;
using Pole.Features.Domains.Application.Migrations;
using Pole.Features.GitOps.Application;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    [Fact]
    public async Task CustomHostnamesBlockManualExposureDowngradeAndRollBackPlacementChanges()
    {
        await EnablePublicWebTargetAsync();
        await new DomainIndexesMigration().Up(Fixture.Database);
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var creation = (await Mediator.Send(TestData.CreateWebServiceRequest(group.Id, "demo-api")))
            .AssertHandlerOutcome();
        if (creation is CreateWebServiceError creationError)
            Assert.Fail($"{creationError.Value.GetType().Name}: " +
                System.Text.Json.JsonSerializer.Serialize(creationError.Value, creationError.Value.GetType()));
        var created = Assert.IsType<CreateWebServiceResponse>(creation.Value);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(HostnameSnapshot())).Status);
        var before = await WebServices.Find(item => item.Id == created.WebServiceId).SingleAsync();
        var placement = await DbContext.ResourceDeployments.Find(item => item.ResourceId == before.Id).SingleAsync();
        var events = await BusinessEvents();
        var releases = await Releases();

        var outcome = (await Mediator.Send(new UpdateWebServiceRequest(before.Id,
            new(before.Spec.ToDto(), before.Tags, WebServiceExposureMode.Private, before.ActiveRevision),
            before.TenantId))).AssertHandlerOutcome();
        var error = Assert.IsType<UpdateWebServiceError>(outcome.Value);
        var unavailable = Assert.IsType<WebServiceDependencyUnavailable>(error.Value);
        Assert.IsType<WebServicePublicExposureRequired>(unavailable.Cause.Value);
        Assert.Equal("CustomHostnamesRequirePublicExposure", WebServiceDependencyDiagnostics.Code(unavailable));
        var unchanged = await WebServices.Find(item => item.Id == before.Id).SingleAsync();
        Assert.Equal(before.Revision, unchanged.Revision);
        Assert.Equal(WebServiceExposureMode.Public, unchanged.Exposure.Mode);
        var unchangedPlacement = await DbContext.ResourceDeployments.Find(item => item.Id == placement.Id)
            .SingleAsync();
        Assert.Equal(placement.Revision, unchangedPlacement.Revision);
        Assert.Equal(placement.DesiredGeneration, unchangedPlacement.DesiredGeneration);
        Assert.Equal(placement.ResolvedPlacement.Ingress!.Hostnames,
            unchangedPlacement.ResolvedPlacement.Ingress!.Hostnames);
        Assert.Equal(events, await BusinessEvents());
        Assert.Equal(releases, await Releases());
    }
}
