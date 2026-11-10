using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Tenant2)]
public sealed class ResourceGroupDriftTests(
    MongoReplicaSetFixture fixture
) : DeclarativePipelineTestBase(fixture)
{
    [Fact]
    public async Task ResourceGroupSameCommitRepairsManagedDriftThenPerformsVerifiedNoOp()
    {
        var files = GroupSnapshot();
        var first = await Apply(files);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, first.Status);
        var groups = Fixture.Database.GetCollection<ResourceGroup>("ResourceGroups");
        var original = await groups.Find(item => item.TenantId == Data.TenantId && item.Name == "drift-group")
            .SingleAsync();
        var downloads = repository.ReadCommits.Count;
        await groups.UpdateOneAsync(
            item => item.Id == original.Id,
            Builders<ResourceGroup>.Update
                .Set(item => item.Description, "private drift value")
                .Inc(item => item.Revision, 1));

        var repaired = await Apply(files);
        Assert.Equal(first.Context!.Authority.Commit, repaired.Context!.Authority.Commit);
        Assert.Equal(downloads, repository.ReadCommits.Count);
        var current = await groups.Find(item => item.Id == original.Id).SingleAsync();
        Assert.Equal(original.Description, current.Description);
        Assert.Equal(original.Revision + 2, current.Revision);
        var queries = Services.GetRequiredService<GitRepositoryQueries>();
        var changed = (await queries.OperationsAsync(Data.TenantId, source, repaired.Context.RunId, 1, 20, default))!
            .Items.Single();
        Assert.Equal(GitPlannedAction.Update, changed.Plan!.Action);
        Assert.Equal<string>(["configuration.description"], changed.Plan.ChangedFields);
        Assert.DoesNotContain("private drift value", JsonSerializer.Serialize(changed));
        var beforeEvents = await BusinessEvents();

        var unchanged = await Apply(files);
        var after = await groups.Find(item => item.Id == original.Id).SingleAsync();
        Assert.Equal(
            current.ToBsonDocument(),
            (after with { RegistrationFence = current.RegistrationFence }).ToBsonDocument());
        Assert.True(after.RegistrationFence > current.RegistrationFence);
        Assert.Equal(beforeEvents, await BusinessEvents());
        var view = await queries.RunAsync(Data.TenantId, source, unchanged.Context!.RunId, default);
        Assert.Equal(RepositoryRunStatus.AlreadyCurrent, view!.Status);
        Assert.Equal(new RepositoryOperationSummary(0, 0, 1, 0, 0, null), view.Operations);
        var inventory = await Inventory.Find(item => item.ResourceId == original.Id).SingleAsync();
        Assert.Equal(GitResourceMaterialization.Created, inventory.Materialization);
        Assert.Equal(inventory.Projections.Desired, inventory.Projections.Applied);
        Assert.Equal(unchanged.Context.Authority.Commit, (await State()).Commits.FullyApplied);
    }

    [Fact]
    public async Task PreviouslyCreatedResourceGroupIsBlockedWhenMissingAndIsNeverRecreated()
    {
        var files = GroupSnapshot();
        var first = await Apply(files);
        var owned = await Inventory.Find(item => item.SourceId == source).SingleAsync();
        await Fixture.Database
            .GetCollection<ResourceGroup>("ResourceGroups")
            .DeleteOneAsync(item => item.Id == owned.ResourceId);
        var before = await BusinessEvents();

        var result = await Apply(files);
        Assert.Equal(RepositoryPipelineStatus.Blocked, result.Status);
        Assert.Equal(before, await BusinessEvents());
        Assert.Equal(
            0,
            await Fixture.Database
                .GetCollection<ResourceGroup>("ResourceGroups")
                .CountDocumentsAsync(item => item.Id == owned.ResourceId));
        var receipt = await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
            .Find(item => item.RunId == result.Context!.RunId)
            .SingleAsync();
        Assert.Equal("DeclarationManagedResourceMissing", receipt.Blocker);
        Assert.Equal(first.Context!.Authority.Commit, (await State()).Commits.FullyApplied);
        var query = await Services.GetRequiredService<GitRepositoryQueries>()
            .SourceAsync(Data.TenantId, source, default);
        Assert.Equal(new RepositoryOperationSummary(0, 0, 0, 1, 0, null), query!.LatestRun!.Operations);
    }
}
