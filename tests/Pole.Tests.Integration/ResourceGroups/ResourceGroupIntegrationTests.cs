using MongoDB.Driver;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Endpoints;
using Pole.Features.ResourceGroups.Domain;
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
}
