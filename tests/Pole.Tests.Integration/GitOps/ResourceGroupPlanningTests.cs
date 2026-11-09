using MongoDB.Driver;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Tenant2)]
public sealed class ResourceGroupPlanningTests(MongoReplicaSetFixture fixture) : DeclarativePipelineTestBase(fixture)
{
    [Fact]
    public async Task Resource_group_planning_reads_normalized_current_fields_without_mutating()
    {
        var declaration = new CompiledDeclaration(new("ResourceGroup", "demo"),
            DeclarationJson.Write(new { description = " Git acceptance ", tags = new[] { "b", "a", "a" } }),
            "hash", [], "test.yaml", 1);
        using var session = await Fixture.Database.Client.StartSessionAsync();
        var planner = Services.GetRequiredService<DeclarativeResourcePlanner>();
        var request = new PlanDeclarationRequest(new(Data.TenantId, source, null), declaration);
        Assert.IsType<CreateDeclarationPlan>(await planner.PlanAsync(request, session, default));
        await Apply();
        var groups = Fixture.Database.GetCollection<ResourceGroup>("ResourceGroups");
        var group = await groups.Find(item => item.TenantId == Data.TenantId && item.Name == "demo").SingleAsync();
        var update = Assert.IsType<UpdateDeclarationPlan>(await planner.PlanAsync(request, session, default));
        Assert.Equal(group.Revision, Assert.Single(update.Current.Version.Documents).Revision);
        Assert.Equal(group.Revision, (await groups.Find(item => item.Id == group.Id).SingleAsync()).Revision);
        request = request with
        {
            Declaration = declaration with
            {
                Spec = DeclarationJson.Write(new { description = " Git acceptance ", tags = Array.Empty<string>() })
            }
        };
        Assert.IsType<NoOpDeclarationPlan>(await planner.PlanAsync(request, session, default));
        Assert.IsType<CreateDeclarationPlan>(await planner.PlanAsync(request with
        {
            Scope = new(Id(), source, null)
        }, session, default));
        Assert.IsType<BlockedDeclarationPlan>(await planner.PlanAsync(request with
        {
            Scope = new(Data.TenantId, source, "different-resource")
        }, session, default));
    }
}
