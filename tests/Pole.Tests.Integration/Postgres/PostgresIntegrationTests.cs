using MongoDB.Driver;
using Microsoft.FSharp.Core;
using Pole.Features.Postgres.Endpoints;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Postgres;

public sealed class PostgresIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Create_Succeeds_WithValidRequest()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());

        var result = await Mediator.Send(TestData.CreatePostgresClusterRequest(resourceGroup.Id));

        Assert.True(result.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.NotNull(response);

        var savedCluster = await DbContext.Postgres.Find(cluster => cluster.Id == response.Id).FirstOrDefaultAsync();
        Assert.NotNull(savedCluster);
        Assert.Equal(resourceGroup.Id, savedCluster.GroupId);
        Assert.Equal("postgres", savedCluster.Username);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("create-postgres-cluster-requested"));
    }

    [Fact]
    public async Task Update_ChangesClusterSpec()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var createResult = await Mediator.Send(TestData.CreatePostgresClusterRequest(resourceGroup.Id));
        Assert.True(createResult.TryGetValue(out var created, out _));

        var updateRequest = new UpdatePostgresClusterRequest(
            created!.Id,
            new UpdatePostgresClusterRequestBody(
                FSharpOption<string>.Some("postgres-updated"),
                FSharpOption<string>.Some(resourceGroup.Id),
                FSharpOption<int>.Some(3),
                FSharpOption<int>.Some(25),
                FSharpOption<string>.Some("app-user"),
                FSharpOption<string>.Some("updated-secret")));

        var updateResult = await Mediator.Send(updateRequest);

        Assert.True(updateResult.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.NotNull(response);
        Assert.Equal(created.Id, response.Id);

        var updatedCluster = await DbContext.Postgres.Find(cluster => cluster.Id == created.Id).FirstOrDefaultAsync();
        Assert.NotNull(updatedCluster);
        Assert.Equal("postgres-updated", updatedCluster.Name);
        Assert.Equal(3, updatedCluster.Spec.Instances);
        Assert.Equal(25, updatedCluster.Spec.Storage);
        Assert.Equal("app-user", updatedCluster.Username);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("update-postgres-cluster-requested"));
    }

    [Fact]
    public async Task GetList_AppliesPagination()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());

        await Mediator.Send(TestData.CreatePostgresClusterRequest(resourceGroup.Id, name: "postgres-alpha"));
        await Mediator.Send(TestData.CreatePostgresClusterRequest(resourceGroup.Id, name: "postgres-beta", username: "beta-user"));

        var result = await Mediator.Send(new GetPostgresClustersRequest
        {
            PageNumber = 2,
            PageSize = 1
        });

        Assert.True(result.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.NotNull(response);
        Assert.Single(response.Items);
        Assert.Equal(2L, response.TotalCount);
        Assert.Equal(2, response.PageNumber);
        Assert.Equal(1, response.PageSize);
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_WhenClusterDoesNotExist()
    {
        var result = await Mediator.Send(new DeletePostgresClusterRequest("507f1f77bcf86cd799439012"));

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.IsType<NotFoundFailure>(error);
    }
}
