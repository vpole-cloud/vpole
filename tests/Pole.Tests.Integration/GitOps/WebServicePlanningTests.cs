using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.Declarations;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Tenant1)]
public sealed class WebServicePlanningTests(MongoReplicaSetFixture fixture) : DeclarativePipelineTestBase(fixture)
{
    [Fact]
    public async Task Web_service_planning_tracks_preparation_configuration_and_deployment_versions()
    {
        var initial = await Apply();
        var declaration = Services.GetRequiredService<ManifestCompiler>().Compile(Snapshot()).Declarations
            .Single(item => item.Address.Kind == "WebService");
        var planner = Services.GetRequiredService<DeclarativeResourcePlanner>();
        var transactions = Services.GetRequiredService<MongoRunTransaction>();
        Task<ResourceDeclarationPlan> PlanAsync(PlanDeclarationRequest input) =>
            transactions.InvokeOutcomeAsync<ResourceDeclarationPlan>(async (tx, ct) =>
                tx.Commit(await planner.PlanAsync(input, tx.Session, ct)), default);
        var request = new PlanDeclarationRequest(new(Data.TenantId, source, null), declaration);
        Assert.IsType<UpdateDeclarationPlan>(await PlanAsync(request));
        await Supply("never-in-web-plan");
        await Pipeline.ResumeAsync(initial.Context!);
        var current = Assert.IsType<NoOpDeclarationPlan>(await PlanAsync(request));
        Assert.Equal(2, current.Current.Version.Documents.Count);
        Assert.DoesNotContain("never-in-web-plan", current.Current.Projection.Configuration.GetRawText());
        var service = await Service();
        var releases = await Releases();
        var deployment = await DbContext.ResourceDeployments.Find(Builders<ResourceDeployment>.Filter.Eq(item => item.TenantId, Data.TenantId))
            .SingleAsync();
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == deployment.Id, deployment with
        {
            ObservedGeneration = deployment.DesiredGeneration,
            State = new AppliedResourceDeploymentState(deployment.DesiredGeneration, DateTime.UtcNow)
        });
        Assert.IsType<NoOpDeclarationPlan>(await PlanAsync(request));
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == deployment.Id, deployment with
        {
            FixedReplicas = deployment.FixedReplicas + 1,
            Revision = deployment.Revision + 1
        });
        Assert.IsType<UpdateDeclarationPlan>(await PlanAsync(request));
        Assert.Equal(service.Revision, (await Service()).Revision);
        Assert.Equal(releases, await Releases());
        Assert.IsType<BlockedDeclarationPlan>(await PlanAsync(request with
        {
            Scope = new(Id(), source, null)
        }));
        await WebServices.DeleteOneAsync(item => item.Id == service.Id);
        Assert.IsType<CreateDeclarationPlan>(await PlanAsync(request));
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
        var planner = Services.GetRequiredService<DeclarativeResourcePlanner>();
        var result = Assert.IsType<BlockedDeclarationPlan>(await Services.GetRequiredService<MongoRunTransaction>()
            .InvokeOutcomeAsync<ResourceDeclarationPlan>(async (tx, ct) => tx.Commit(await planner.PlanAsync(
                new(new(Data.TenantId, source, null), declaration), tx.Session, ct)), default));
        Assert.Equal(WebServiceDeclarationError.ComputeProfileRevisionUnavailable,
            Assert.IsType<WebServiceDeclarationRejected>(result.Failure).Reason);
    }
}
