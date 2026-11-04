using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Endpoints;
using Pole.Features.ResourceGroups.Application.Migrations;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    private static ManifestFile[] IsolationSnapshot(string group = "scope-group") =>
    [
        new("scope.yaml", $$"""
            apiVersion: pole.io/v1alpha1
            kind: IsolationScope
            metadata:
              name: tenant-private
            spec:
              resourceGroup: {{group}}
            ---
            apiVersion: pole.io/v1alpha1
            kind: ResourceGroup
            metadata:
              name: {{group}}
            spec: {}
            """)
    ];

    [Fact]
    public async Task IsolationScopeCreatesAfterGroupThenNoOpsAndRejectsManualDeletion()
    {
        await new ResourceGroupPlacementMigration().Up(Fixture.Database);
        var files = IsolationSnapshot();
        var created = await Apply(files);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, created.Status);
        var scope = await DbContext.IsolationScopes.Find(item => item.Name == "tenant-private").SingleAsync();
        var inventory = await Inventory.Find(item => item.ResourceId == scope.Id).SingleAsync();
        Assert.Equal(GitResourceMaterialization.Created, inventory.Materialization);
        var events = await BusinessEvents();

        var unchanged = await Apply(files);
        Assert.Equal(events, await BusinessEvents());
        Assert.Equal(scope, await DbContext.IsolationScopes.Find(item => item.Id == scope.Id).SingleAsync());
        var view = await Services.GetRequiredService<GitRepositoryQueries>().RunAsync(TestData.TenantId, source,
            unchanged.Context!.RunId, default);
        Assert.Equal(RepositoryRunStatus.AlreadyCurrent, view!.Status);
        Assert.Equal(2, view.Operations!.NoOp);

        var detail = (await Mediator.Send(new GetIsolationScopeRequest(scope.ResourceGroupId, scope.Id,
            scope.TenantId))).AssertHandlerOutcome();
        Assert.NotNull(Assert.IsType<IsolationScopeDto>(detail.Value).GitOps);
        var deletion = (await Mediator.Send(new DeleteIsolationScopeRequest(scope.ResourceGroupId, scope.Id,
            scope.TenantId))).AssertHandlerOutcome();
        var error = Assert.IsType<Pole.Features.ResourceGroups.Application.Persistence.IsolationScopes
            .IsolationScopeDeletionError>(deletion.Value);
        Assert.Equal(ResourceGroupRevisionConflictReason.GitOwnedConfiguration,
            Assert.IsType<ResourceGroupRevisionConflict>(error.Value).Reason);
        Assert.Equal(events, await BusinessEvents());
    }

    [Fact]
    public async Task IsolationScopeIsNotRecreatedOrReboundAfterNameReuse()
    {
        var files = IsolationSnapshot();
        await Apply(files);
        var original = await DbContext.IsolationScopes.Find(item => item.Name == "tenant-private").SingleAsync();
        await DbContext.IsolationScopes.DeleteOneAsync(item => item.Id == original.Id);
        var result = await Apply(files);
        Assert.Equal(RepositoryPipelineStatus.Blocked, result.Status);
        Assert.Equal(0, await DbContext.IsolationScopes.CountDocumentsAsync(item => item.Id == original.Id));
        var receipt = await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
            .Find(item => item.RunId == result.Context!.RunId && item.ResourceId == original.Id).SingleAsync();
        Assert.Equal(nameof(Pole.Features.GitOps.Contracts.DeclarationManagedResourceMissing), receipt.Blocker);

        var replacement = IsolationScopeFactory.CreateAdditional(original.TenantId, original.ResourceGroupId,
            original.Name, IsolationScopeFactory.DefaultOptions);
        await DbContext.IsolationScopes.InsertOneAsync(replacement);
        result = await Apply(files);
        Assert.Equal(RepositoryPipelineStatus.Blocked, result.Status);
        var owned = await Inventory.Find(item => item.ResourceId == original.Id).SingleAsync();
        Assert.Equal(original.Id, owned.ResourceId);
    }

    [Fact]
    public async Task IsolationScopeOwnershipCannotBeBypassedByParentDeletion()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("scope-group"));
        var files = IsolationSnapshot();
        files = [files[0] with { Content = files[0].Content.Split("---", StringSplitOptions.None)[0] }];
        var applied = await Apply(files);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, applied.Status);
        var result = await Services.GetRequiredService<Pole.Features.ResourceGroups.Application.Persistence
            .ResourceGroups.BeginResourceGroupDeletion>()(group.TenantId, group.Id, default);
        var error = Assert.IsType<Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups
            .ResourceGroupDeletionError>(result.Value);
        Assert.Equal(ResourceGroupRevisionConflictReason.GitOwnedConfiguration,
            Assert.IsType<ResourceGroupRevisionConflict>(error.Value).Reason);
        Assert.Equal(2, await DbContext.IsolationScopes.CountDocumentsAsync(item => item.ResourceGroupId == group.Id));
    }

    [Fact]
    public async Task IsolationScopeCannotMoveToAnotherGroup()
    {
        await Apply(IsolationSnapshot());
        var original = await DbContext.IsolationScopes.Find(item => item.Name == "tenant-private").SingleAsync();
        var changed = await Apply(IsolationSnapshot("another-group"), commit: "changed-parent");
        Assert.Equal(RepositoryPipelineStatus.Blocked, changed.Status);
        Assert.Equal(original.ResourceGroupId,
            (await DbContext.IsolationScopes.Find(item => item.Id == original.Id).SingleAsync()).ResourceGroupId);
    }
}
