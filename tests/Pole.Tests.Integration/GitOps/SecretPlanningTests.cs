using MongoDB.Driver;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    [Fact]
    public async Task Secret_planning_ignores_value_rotation_and_reports_metadata_drift()
    {
        await Apply();
        var declaration = Services.GetRequiredService<ManifestCompiler>().Compile(Snapshot()).Declarations
            .Single(item => item.Address.Kind == "Secret");
        using var session = await Fixture.Database.Client.StartSessionAsync();
        var planner = Services.GetRequiredService<DeclarativeResourcePlanner>();
        var request = new PlanDeclarationRequest(new(TestData.TenantId, source, null), declaration);
        var before = Assert.IsType<NoOpDeclarationPlan>(await planner.PlanAsync(request, session, default));
        await Supply("must-never-appear-in-a-plan");
        var after = Assert.IsType<NoOpDeclarationPlan>(await planner.PlanAsync(request, session, default));
        Assert.True(System.Text.Json.JsonElement.DeepEquals(before.Current.Projection.Configuration,
            after.Current.Projection.Configuration));
        Assert.DoesNotContain("must-never-appear", after.Current.Projection.Configuration.GetRawText());
        Assert.DoesNotContain("currentVersion", after.Current.Projection.Configuration.GetRawText());
        var secret = await Secrets.Find(Builders<Secret>.Filter.Empty).SingleAsync();
        await Secrets.ReplaceOneAsync(item => item.Id == secret.Id, secret with
        {
            Purpose = "drift",
            Revision = secret.Revision + 1
        });
        Assert.IsType<UpdateDeclarationPlan>(await planner.PlanAsync(request, session, default));
        Assert.IsType<BlockedDeclarationPlan>(await planner.PlanAsync(request with
        {
            Scope = new(Id(), source, null)
        }, session, default));
        await Secrets.DeleteOneAsync(item => item.Id == secret.Id);
        Assert.IsType<CreateDeclarationPlan>(await planner.PlanAsync(request, session, default));
    }
}
