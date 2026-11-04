using MongoDB.Driver;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    [Fact]
    public async Task Web_service_planning_tracks_preparation_configuration_and_deployment_versions()
    {
        var initial = await Apply();
        var declaration = Services.GetRequiredService<ManifestCompiler>().Compile(Snapshot()).Declarations
            .Single(item => item.Address.Kind == "WebService");
        using var session = await Fixture.Database.Client.StartSessionAsync();
        var planner = Services.GetRequiredService<DeclarativeResourcePlanner>();
        var request = new PlanDeclarationRequest(new(TestData.TenantId, source, null), declaration);
        Assert.IsType<UpdateDeclarationPlan>(await planner.PlanAsync(request, session, default));
        await Supply("never-in-web-plan");
        await Pipeline.ResumeAsync(initial.Context!);
        var current = Assert.IsType<NoOpDeclarationPlan>(await planner.PlanAsync(request, session, default));
        Assert.Equal(2, current.Current.Version.Documents.Count);
        Assert.DoesNotContain("never-in-web-plan", current.Current.Projection.Configuration.GetRawText());
        var service = await Service();
        var releases = await Releases();
        var deployment = await DbContext.ResourceDeployments.Find(Builders<ResourceDeployment>.Filter.Empty)
            .SingleAsync();
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == deployment.Id, deployment with
        {
            ObservedGeneration = deployment.DesiredGeneration,
            State = new AppliedResourceDeploymentState(deployment.DesiredGeneration, DateTime.UtcNow)
        });
        Assert.IsType<NoOpDeclarationPlan>(await planner.PlanAsync(request, session, default));
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == deployment.Id, deployment with
        {
            FixedReplicas = deployment.FixedReplicas + 1,
            Revision = deployment.Revision + 1
        });
        Assert.IsType<UpdateDeclarationPlan>(await planner.PlanAsync(request, session, default));
        Assert.Equal(service.Revision, (await Service()).Revision);
        Assert.Equal(releases, await Releases());
        Assert.IsType<BlockedDeclarationPlan>(await planner.PlanAsync(request with
        {
            Scope = new(Id(), source, null)
        }, session, default));
        await WebServices.DeleteOneAsync(item => item.Id == service.Id);
        Assert.IsType<CreateDeclarationPlan>(await planner.PlanAsync(request, session, default));
    }
    [Fact]
    public async Task Web_service_plan_blocks_a_missing_revision_of_the_pinned_profile()
    {
        var initial = await Apply();
        await Supply();
        await Pipeline.ResumeAsync(initial.Context!);
        var declaration = Services.GetRequiredService<ManifestCompiler>()
            .Compile(Snapshot(profileRevision: long.MaxValue)).Declarations
            .Single(item => item.Address.Kind == "WebService");
        using var session = await Fixture.Database.Client.StartSessionAsync();
        var result = Assert.IsType<BlockedDeclarationPlan>(await Services
            .GetRequiredService<DeclarativeResourcePlanner>().PlanAsync(
                new(new(TestData.TenantId, source, null), declaration), session, default));
        Assert.Equal(WebServiceDeclarationError.ComputeProfileRevisionUnavailable,
            Assert.IsType<WebServiceDeclarationRejected>(result.Failure).Reason);
    }
}
