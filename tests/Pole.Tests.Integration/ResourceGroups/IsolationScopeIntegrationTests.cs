using MongoDB.Driver;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Endpoints;
using Pole.Features.ResourceGroups.Application.Migrations;
using Pole.Features.ResourceGroups.Domain;
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
    }

    [Fact]
    public async Task CreateListAndGet_UseResourceGroupAndTenantOwnership()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var createResult = await Mediator.Send(new CreateIsolationScopeRequest(
            resourceGroup.Id,
            new CreateIsolationScopeRequestBody("Production"),
            resourceGroup.TenantId));

        Assert.True(createResult.TryGetValue(out IsolationScopeDto? created, out Failure? createFailure));
        Assert.Null(createFailure);
        Assert.NotNull(created);
        Assert.False(created.IsDefault);
        Assert.Equal("Production", created.Name);

        var listResult = await Mediator.Send(new GetIsolationScopesRequest(
            resourceGroup.Id,
            resourceGroup.TenantId));
        Assert.True(listResult.TryGetValue(out var scopes, out var listFailure));
        Assert.Null(listFailure);
        Assert.Equal(2, scopes.Count);
        Assert.True(scopes[0].IsDefault);
        Assert.Equal(created.Id, scopes[1].Id);

        var getResult = await Mediator.Send(new GetIsolationScopeRequest(
            resourceGroup.Id,
            created.Id,
            resourceGroup.TenantId));
        Assert.True(getResult.TryGetValue(out var found, out var getFailure));
        Assert.Null(getFailure);
        Assert.Equal(created.Id, found.Id);
        Assert.Equal(created.ResourceGroupId, found.ResourceGroupId);
        Assert.Equal(created.Name, found.Name);
        Assert.Equal(created.IsDefault, found.IsDefault);
        Assert.Equal(created.Lifecycle, found.Lifecycle);
        Assert.Equal(created.Revision, found.Revision);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("isolation-scope-created"));
    }

    [Fact]
    public async Task Create_ReturnsConflict_ForDuplicateNormalizedName()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var first = await Mediator.Send(new CreateIsolationScopeRequest(
            resourceGroup.Id,
            new CreateIsolationScopeRequestBody("Production"),
            resourceGroup.TenantId));
        var duplicate = await Mediator.Send(new CreateIsolationScopeRequest(
            resourceGroup.Id,
            new CreateIsolationScopeRequestBody(" production "),
            resourceGroup.TenantId));

        Assert.IsType<Success<IsolationScopeDto>>(first);
        Assert.False(duplicate.TryGetValue(out _, out var failure));
        Assert.IsType<ConflictFailure>(failure);
        Assert.Equal(2L, await DbContext.IsolationScopes.CountDocumentsAsync(
            scope => scope.ResourceGroupId == resourceGroup.Id));
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("isolation-scope-created"));
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

        Assert.False(crossGroup.TryGetValue(out _, out var crossGroupFailure));
        Assert.IsType<NotFoundFailure>(crossGroupFailure);
        Assert.False(crossTenant.TryGetValue(out _, out var crossTenantFailure));
        Assert.IsType<NotFoundFailure>(crossTenantFailure);
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
            index["name"] == "UX_IsolationScopes_ResourceGroup_Name" &&
            index["unique"].AsBoolean);
        Assert.Contains(cellIndexes, index =>
            index["name"] == "UX_RuntimeCells_Scope_Target" &&
            index["unique"].AsBoolean);
        Assert.Contains(cellIndexes, index =>
            index["name"] == "IX_RuntimeCells_State_Retry");
    }
}
