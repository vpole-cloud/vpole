using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Endpoints;
using Pole.Features.ResourceGroups.Application.Services;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Shared.Models;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceGroups;

public sealed class ResourceGroupIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Create_PersistsResourceGroup_AndWritesOutboxMessage()
    {
        var request = TestData.CreateResourceGroupRequest();

        var result = await Mediator.Send(request);

        Assert.True(result.TryGetValue(out ResourceGroupDto? created, out Failure? error));
        Assert.Null(error);
        Assert.NotNull(created);

        var saved = await DbContext.ResourceGroups.Find(group => group.Id == created.Id).FirstOrDefaultAsync();
        Assert.NotNull(saved);
        Assert.Equal(request.Body.Name, saved.Name);
        Assert.StartsWith("test-", saved.KubernetesNamespace);
        Assert.Equal(created.DefaultIsolationScopeId, saved.DefaultIsolationScopeId);
        Assert.Equal("active", created.Lifecycle);
        Assert.Equal(saved.Revision, created.Revision);

        var defaultScope = await DbContext.IsolationScopes
            .Find(scope => scope.ResourceGroupId == saved.Id)
            .SingleOrDefaultAsync();
        Assert.NotNull(defaultScope);
        Assert.Equal(saved.DefaultIsolationScopeId, defaultScope.Id);
        Assert.True(defaultScope.IsDefault);
        Assert.Equal(IsolationScopeFactory.DefaultScopeName, defaultScope.Name);

        var outboxCount = await DbContext.CountOutboxMessagesAsync("resource-group-created");
        Assert.Equal(1L, outboxCount);
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync("isolation-scope-created"));
    }

    [Fact]
    public async Task Get_ReturnsSeededGroups_AndSupportsSearch()
    {
        await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("alpha"));
        await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("beta"));

        var result = await Mediator.Send(new GetResourceGroupsRequest
        {
            Search = "alp",
            PageNumber = 1,
            PageSize = 10
        });

        result.TryGetValue(out var response, out var error);
        Assert.Null(error);
        Assert.NotNull(response);
        Assert.Single(response.Items);
        Assert.Equal("alpha", response.Items.Single().Name);
    }

    [Fact]
    public async Task Delete_RemovesResourceGroup_AndWritesDeleteEvent()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());

        var result = await Mediator.Send(new DeleteResourceGroupRequest(resourceGroup.Id));

        result.TryGetValue(out var response, out var error);
        Assert.Null(error);
        Assert.NotNull(response);
        Assert.True(response.Deleted);

        var deleted = await DbContext.ResourceGroups.Find(group => group.Id == resourceGroup.Id).FirstOrDefaultAsync();
        Assert.Null(deleted);
        Assert.False(await DbContext.IsolationScopes
            .Find(scope => scope.ResourceGroupId == resourceGroup.Id)
            .AnyAsync());

        var outboxCount = await DbContext.CountOutboxMessagesAsync("resource-group-deleted");
        Assert.Equal(1L, outboxCount);
    }

    [Fact]
    public async Task Delete_ReturnsConflict_WhenLegacyResourcesAreRegistered()
    {
        var seeded = TestData.SeedResourceGroup();
        var resourceGroup = await DbContext.InsertResourceGroupAsync(seeded with
        {
            Resources = ImmutableList.Create(
                new ShortResourceInfo(
                    "507f1f77bcf86cd799439081",
                    "legacy-resource",
                    ResourceType.WebService))
        });

        var result = await Mediator.Send(new DeleteResourceGroupRequest(resourceGroup.Id));

        Assert.False(result.TryGetValue(out _, out var failure));
        Assert.IsType<ConflictFailure>(failure);
        Assert.True(await DbContext.ResourceGroups
            .Find(group => group.Id == resourceGroup.Id)
            .AnyAsync());
    }

    [Fact]
    public async Task Delete_WithRuntimeCell_RequestsAggregateDeletion()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var cell = RuntimeCellFactory.Create(
            resourceGroup.TenantId,
            resourceGroup.Id,
            resourceGroup.DefaultIsolationScopeId,
            "507f1f77bcf86cd799439071",
            "pole-delete-resource-group",
            RuntimeCellFactory.DefaultOptions);
        await DbContext.RuntimeCells.InsertOneAsync(cell);

        var result = await Mediator.Send(new DeleteResourceGroupRequest(resourceGroup.Id));

        Assert.True(result.TryGetValue(
            out DeleteResourceGroupResponse? response,
            out Failure? failure));
        Assert.Null(failure);
        Assert.NotNull(response);
        Assert.False(response.Deleted);
        Assert.True(response.DeletionRequested);
        var savedGroup = await DbContext.ResourceGroups
            .Find(group => group.Id == resourceGroup.Id)
            .SingleAsync();
        Assert.IsType<DeletingResourceGroupLifecycleState>(savedGroup.Lifecycle);
        Assert.Equal("deleting", savedGroup.ToDto().Lifecycle);
        var savedScope = await DbContext.IsolationScopes
            .Find(scope => scope.Id == resourceGroup.DefaultIsolationScopeId)
            .SingleAsync();
        Assert.IsType<DeletingIsolationScopeLifecycleState>(savedScope.Lifecycle);
        var savedCell = await DbContext.RuntimeCells
            .Find(item => item.Id == cell.Id)
            .SingleAsync();
        Assert.IsType<DeletingRuntimeCellState>(savedCell.State);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(
            "runtime-cell-deletion-requested"));
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync(
            "resource-group-deleted"));
    }

    [Fact]
    public async Task TransitionalPlacement_RejectsDeletingResourceGroup()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        await DbContext.ResourceGroups.ReplaceOneAsync(
            group => group.Id == resourceGroup.Id,
            resourceGroup.RequestDeletion(resourceGroup.UpdatedAtUtc.AddMinutes(1)));
        var lookup = new ResourceGroupLookup(DbContext);

        var result = await lookup.GetPlacementAsync(
            resourceGroup.TenantId,
            resourceGroup.Id);

        Assert.False(result.TryGetValue(out _, out var failure));
        Assert.IsType<ConflictFailure>(failure);
    }
}
