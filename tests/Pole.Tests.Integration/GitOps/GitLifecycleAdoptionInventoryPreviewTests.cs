using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class GitLifecyclePreviewProjectionTests
{
    [Fact]
    public async Task Explicit_adoption_of_an_already_created_same_source_resource_is_rejected_before_confirmation()
    {
        var source = await Create();
        var id = await Own(source, widgets, "owned");
        repository.Files = [Manifest(widgets.Kind, "owned")];
        var before = await Sources.StateAsync(source.TenantId, source.Id, default);
        var result = await Previews.CreateAsync(
            new CreateLifecyclePreview(
                new GitSourceIdentity(source.TenantId, source.Id),
                source.Revision,
                new GitPreviewAdoption([new GitResourceIdentity(widgets.Kind, "owned", id)])),
            default);
        Assert.IsType<GitDeclarationOwnershipConflict>(Assert.IsType<GitPreviewRejected>(result.Value).Failure);
        Assert.Equal(
            0,
            await Services.GetRequiredService<IMongoCollection<GitOperationPreview>>().CountDocumentsAsync(_ => true));
        var after = await Sources.StateAsync(source.TenantId, source.Id, default);
        Assert.Equal(before.RequestedGeneration, after.RequestedGeneration);
        Assert.Equal(before.InventoryRevision, after.InventoryRevision);
        Assert.Equal(
            GitResourceMaterialization.Created,
            (await Inventory.Find(_ => true).SingleAsync()).Materialization);
        Assert.Equal(0, widgets.ApplyCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Allocated_inventory_allows_the_first_real_adoption_without_recreating_the_resource(
        bool sameReservedId)
    {
        var source = await Create();
        var reserved = await Own(source, widgets, "manual", GitResourceMaterialization.Allocated);
        var manual = sameReservedId ? reserved : ObjectId.GenerateNewId().ToString();
        if (!sameReservedId)
        {
            await widgets.Documents.DeleteOneAsync(_ => true);
            await widgets.Documents.InsertOneAsync(
                Document(source.TenantId, manual, "manual", "identity", "value", null, 1));
        }

        var before = await widgets.Documents.Find(_ => true).SingleAsync();
        repository.Files = [Manifest(widgets.Kind, "manual")];
        var preview = Assert.IsType<GitOperationPreview>(
            (await Previews.CreateAsync(
                new CreateLifecyclePreview(
                    new GitSourceIdentity(source.TenantId, source.Id),
                    source.Revision,
                    new GitPreviewAdoption([new GitResourceIdentity(widgets.Kind, "manual", manual)])),
                default)).Value);
        Assert.Equal(GitLifecycleAction.Adopt, Assert.Single(preview.Operations).Action);
        await ConfirmAndRun(source, preview);
        var owned = await Inventory.Find(_ => true).SingleAsync();
        Assert.Equal(manual, owned.ResourceId);
        Assert.Equal(GitResourceMaterialization.Created, owned.Materialization);
        var current = await widgets.Documents.Find(_ => true).SingleAsync();
        Assert.Equal(before["_id"], current["_id"]);
        Assert.Equal(before["Revision"], current["Revision"]);
        Assert.Equal(before["Value"], current["Value"]);
        Assert.Equal(0, widgets.ApplyCalls);
        Assert.Equal(0, widgets.Deletions);
    }
}
