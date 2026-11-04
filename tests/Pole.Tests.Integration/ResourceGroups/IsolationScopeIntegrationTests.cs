using MongoDB.Driver;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Endpoints;
using Pole.Features.ResourceGroups.Application.Migrations;
using Pole.Features.ResourceGroups.Application.Persistence;
using Pole.Features.ResourceGroups.Application.Persistence.IsolationScopes;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Shared.Models;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceGroups;

public sealed class IsolationScopeIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new ResourceGroupPlacementMigration().Up(Fixture.Database);
        await new RuntimeCellLookupMigration().Up(Fixture.Database);
    }

    [Fact]
    public async Task CreateListAndGet_UseResourceGroupAndTenantOwnership()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var createResult = await Mediator.Send(new CreateIsolationScopeRequest(
            resourceGroup.Id,
            new CreateIsolationScopeRequestBody("production"),
            resourceGroup.TenantId));

        var created = AssertCreated(createResult.AssertHandlerOutcome());
        Assert.False(created.IsDefault);
        Assert.Equal("production", created.Name);

        var listResult = await Mediator.Send(new GetIsolationScopesRequest(
            resourceGroup.Id,
            resourceGroup.TenantId));
        var scopes = AssertScopes(listResult.AssertHandlerOutcome());
        Assert.Equal(2, scopes.Count);
        Assert.True(scopes[0].IsDefault);
        Assert.Equal(created.Id, scopes[1].Id);

        var getResult = await Mediator.Send(new GetIsolationScopeRequest(
            resourceGroup.Id,
            created.Id,
            resourceGroup.TenantId));
        var found = AssertScope(getResult.AssertHandlerOutcome());
        Assert.Equal(created.Id, found.Id);
        Assert.Equal(created.ResourceGroupId, found.ResourceGroupId);
        Assert.Equal(created.Name, found.Name);
        Assert.Equal(created.IsDefault, found.IsDefault);
        Assert.Equal(created.Lifecycle, found.Lifecycle);
        Assert.Equal(created.Revision, found.Revision);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("pole.resource-groups/v1/isolation-scope-created"));
    }

    [Fact]
    public async Task Create_ReturnsConflict_ForDuplicateNormalizedName()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var first = await Mediator.Send(new CreateIsolationScopeRequest(
            resourceGroup.Id,
            new CreateIsolationScopeRequestBody("production"),
            resourceGroup.TenantId));
        var duplicate = await Mediator.Send(new CreateIsolationScopeRequest(
            resourceGroup.Id,
            new CreateIsolationScopeRequestBody("production"),
            resourceGroup.TenantId));

        _ = AssertCreated(first.AssertHandlerOutcome());
        var failure = duplicate.AssertHandlerOutcome() switch
        {
            CreateIsolationScopeError error => error,
            _ => throw new Xunit.Sdk.XunitException("Expected isolation-scope creation failure.")
        };
        Assert.True(failure is IsolationScopeNameConflict);
        Assert.Equal(2L, await DbContext.IsolationScopes.CountDocumentsAsync(
            scope => scope.ResourceGroupId == resourceGroup.Id));
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("pole.resource-groups/v1/isolation-scope-created"));
    }

    [Fact]
    public async Task Get_ReturnsSafeNotFound_ForDifferentResourceGroupOrTenant()
    {
        var firstGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("first"));
        var secondGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("second"));
        var scope = IsolationScopeFactory.CreateAdditional(
            firstGroup.TenantId,
            firstGroup.Id,
            "private",
            IsolationScopeFactory.DefaultOptions);
        await DbContext.IsolationScopes.InsertOneAsync(scope);

        var crossGroup = await Mediator.Send(new GetIsolationScopeRequest(
            secondGroup.Id,
            scope.Id,
            firstGroup.TenantId));
        var crossTenant = await Mediator.Send(new GetIsolationScopeRequest(
            firstGroup.Id,
            scope.Id,
            "507f1f77bcf86cd799439012"));

        Assert.True(crossGroup.AssertHandlerOutcome() is ResourceGroupNotFound or IsolationScopeNotFound);
        Assert.True(crossTenant.AssertHandlerOutcome() is ResourceGroupNotFound or IsolationScopeNotFound);
    }

    [Fact]
    public async Task PlatformAdminReads_UseExplicitTenantFromRequest()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var scope = IsolationScopeFactory.CreateAdditional(
            resourceGroup.TenantId,
            resourceGroup.Id,
            "platform-read",
            IsolationScopeFactory.DefaultOptions);
        await DbContext.IsolationScopes.InsertOneAsync(scope);
        var cell = RuntimeCellFactory.Create(
            resourceGroup.TenantId,
            resourceGroup.Id,
            scope.Id,
            "507f1f77bcf86cd799439071",
            "pole-platform-read",
            RuntimeCellFactory.DefaultOptions);
        await DbContext.RuntimeCells.InsertOneAsync(cell);

        var scopesResult = await Mediator.Send(
            new GetIsolationScopesRequest(resourceGroup.Id, resourceGroup.TenantId));
        var scopeResult = await Mediator.Send(
            new GetIsolationScopeRequest(
                resourceGroup.Id,
                scope.Id,
                resourceGroup.TenantId));
        var cellsResult = await Mediator.Send(
            new GetRuntimeCellsRequest(resourceGroup.Id, resourceGroup.TenantId));
        var cellResult = await Mediator.Send(
            new GetRuntimeCellRequest(
                resourceGroup.Id,
                cell.Id,
                resourceGroup.TenantId));

        var scopes = AssertScopes(scopesResult.AssertHandlerOutcome());
        Assert.Contains(scopes, item => item.Id == scope.Id);
        var foundScope = AssertScope(scopeResult.AssertHandlerOutcome());
        Assert.Equal(scope.Id, foundScope.Id);
        var cells = AssertRuntimeCells(cellsResult.AssertHandlerOutcome());
        Assert.Contains(cells, item => item.Id == cell.Id);
        var foundCell = AssertRuntimeCell(cellResult.AssertHandlerOutcome());
        Assert.Equal(cell.Id, foundCell.Id);
    }

    [Fact]
    public async Task Delete_EmptyAdditionalScope_RemovesItImmediately()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var scope = IsolationScopeFactory.CreateAdditional(
            resourceGroup.TenantId,
            resourceGroup.Id,
            "temporary",
            IsolationScopeFactory.DefaultOptions);
        await DbContext.IsolationScopes.InsertOneAsync(scope);

        var result = await Mediator.Send(new DeleteIsolationScopeRequest(
            resourceGroup.Id,
            scope.Id,
            resourceGroup.TenantId));

        Assert.True(result.AssertHandlerOutcome() is IsolationScopeDeletionCompleted);
        Assert.False(await DbContext.IsolationScopes
            .Find(item => item.Id == scope.Id)
            .AnyAsync());
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(
            "pole.resource-groups/v1/isolation-scope-deletion-requested"));
    }

    [Fact]
    public async Task Delete_DefaultScope_ReturnsConflict()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());

        var result = await Mediator.Send(new DeleteIsolationScopeRequest(
            resourceGroup.Id,
            resourceGroup.DefaultIsolationScopeId,
            resourceGroup.TenantId));

        var failure = result.AssertHandlerOutcome() switch
        {
            IsolationScopeDeletionError error => error,
            _ => throw new Xunit.Sdk.XunitException("Expected isolation-scope deletion failure.")
        };
        Assert.True(failure is DefaultIsolationScopeDeletionForbidden);
        Assert.True(await DbContext.IsolationScopes
            .Find(scope => scope.Id == resourceGroup.DefaultIsolationScopeId)
            .AnyAsync());
    }

    [Fact]
    public async Task Delete_ScopeWithRuntimeCells_RequestsAsynchronousDeletion()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var scope = IsolationScopeFactory.CreateAdditional(
            resourceGroup.TenantId,
            resourceGroup.Id,
            "private",
            IsolationScopeFactory.DefaultOptions);
        await DbContext.IsolationScopes.InsertOneAsync(scope);
        var cells = new[]
        {
            RuntimeCellFactory.Create(
                resourceGroup.TenantId,
                resourceGroup.Id,
                scope.Id,
                "507f1f77bcf86cd799439071",
                "pole-private-a",
                RuntimeCellFactory.DefaultOptions),
            RuntimeCellFactory.Create(
                resourceGroup.TenantId,
                resourceGroup.Id,
                scope.Id,
                "507f1f77bcf86cd799439072",
                "pole-private-b",
                RuntimeCellFactory.DefaultOptions)
        };
        await DbContext.RuntimeCells.InsertManyAsync(cells);

        var result = await Mediator.Send(new DeleteIsolationScopeRequest(
            resourceGroup.Id,
            scope.Id,
            resourceGroup.TenantId));

        Assert.True(result.AssertHandlerOutcome() is IsolationScopeDeletionRequested);
        var savedScope = await DbContext.IsolationScopes
            .Find(item => item.Id == scope.Id)
            .SingleAsync();
        Assert.IsType<DeletingIsolationScopeLifecycleState>(savedScope.Lifecycle);
        var savedCells = await DbContext.RuntimeCells
            .Find(item => item.IsolationScopeId == scope.Id)
            .ToListAsync();
        Assert.Equal(2, savedCells.Count);
        Assert.All(savedCells, cell =>
            Assert.IsType<DeletingRuntimeCellState>(cell.State));
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(
            "pole.resource-groups/v1/isolation-scope-deletion-requested"));
        Assert.Equal(2L, await DbContext.CountOutboxMessagesAsync(
            "pole.resource-groups/v1/runtime-cell-deletion-requested"));
        var deletionRequests = await DbContext.OutboxMessages
            .Find(message =>
                message.EventType == "pole.resource-groups/v1/runtime-cell-deletion-requested")
            .ToListAsync();
        Assert.All(deletionRequests, request =>
        {
            Assert.IsType<PendingOutboxMessageState>(request.State);
            Assert.Contains(savedCells, cell =>
                request.PendingKey == RuntimeCellPendingKey.Deletion(
                    cell.Id,
                    cell.DesiredGeneration));
        });
    }

    [Fact]
    public async Task Migration_CreatesExpectedIndexes()
    {
        var scopeIndexes = await (await DbContext.IsolationScopes.Indexes.ListAsync())
            .ToListAsync();
        var cellIndexes = await (await DbContext.RuntimeCells.Indexes.ListAsync())
            .ToListAsync();

        Assert.Contains(scopeIndexes, index =>
            index["name"] == "UX_IsolationScopes_ResourceGroup_Default" &&
            index["unique"].AsBoolean);
        Assert.Contains(scopeIndexes, index =>
            index["name"] == "UX_IsolationScopes_Tenant_Name" &&
            index["unique"].AsBoolean);
        Assert.Contains(scopeIndexes, index =>
            index["name"] == "IX_IsolationScopes_Lifecycle_UpdatedAt");
        Assert.Contains(cellIndexes, index =>
            index["name"] == "UX_RuntimeCells_Scope_Target" &&
            index["unique"].AsBoolean);
        Assert.Contains(cellIndexes, index =>
            index["name"] == "IX_RuntimeCells_State_Retry");
        Assert.Contains(cellIndexes, index =>
            index["name"] == "IX_RuntimeCells_State_UpdatedAt");
        Assert.Contains(cellIndexes, index =>
            index["name"] == "IX_RuntimeCells_Tenant_Id");
    }

    private static IsolationScopeDto AssertCreated(CreateIsolationScopeOutcome outcome) => outcome switch
    {
        IsolationScopeDto success => success,
        CreateIsolationScopeError error => throw new Xunit.Sdk.XunitException(
            $"Expected isolation-scope creation success, got {error.GetType().Name}.")
    };

    private static IReadOnlyList<IsolationScopeDto> AssertScopes(
        GetIsolationScopesOutcome outcome) => outcome switch
        {
            IReadOnlyList<IsolationScopeDto> success => success,
            ResourceGroupNotFound error => throw new Xunit.Sdk.XunitException(
                $"Expected isolation scopes, resource group '{error.ResourceGroupId}' was not found.")
        };

    private static IsolationScopeDto AssertScope(GetIsolationScopeOutcome outcome) => outcome switch
    {
        IsolationScopeDto success => success,
        ResourceGroupNotFound error => throw new Xunit.Sdk.XunitException(
            $"Expected isolation scope, resource group '{error.ResourceGroupId}' was not found."),
        IsolationScopeNotFound error => throw new Xunit.Sdk.XunitException(
            $"Expected isolation scope '{error.IsolationScopeId}' to exist.")
    };

    private static IReadOnlyList<RuntimeCellDto> AssertRuntimeCells(
        GetRuntimeCellsOutcome outcome) => outcome switch
        {
            IReadOnlyList<RuntimeCellDto> success => success,
            ResourceGroupNotFound error => throw new Xunit.Sdk.XunitException(
                $"Expected runtime cells, resource group '{error.ResourceGroupId}' was not found.")
        };

    private static RuntimeCellDto AssertRuntimeCell(GetRuntimeCellOutcome outcome) => outcome switch
    {
        RuntimeCellDto success => success,
        ResourceGroupNotFound error => throw new Xunit.Sdk.XunitException(
            $"Expected runtime cell, resource group '{error.ResourceGroupId}' was not found."),
        RuntimeCellNotFound error => throw new Xunit.Sdk.XunitException(
            $"Expected runtime cell '{error.RuntimeCellId}' to exist.")
    };
}
