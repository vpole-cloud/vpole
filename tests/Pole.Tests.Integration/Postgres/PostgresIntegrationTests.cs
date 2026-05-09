using MongoDB.Driver;
using Microsoft.FSharp.Core;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Postgres.Domain;
using Pole.Features.Postgres.Endpoints;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Postgres;

public sealed class PostgresIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private InMemoryPostgresClusterSecretStore SecretStore => Services.GetRequiredService<InMemoryPostgresClusterSecretStore>();

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
        Assert.Equal(resourceGroup.KubernetesNamespace, savedCluster.Spec.KubernetesNamespace);
        Assert.Equal(17, savedCluster.Spec.Version);
        Assert.Equal(PostgresDeploymentMode.Operator, savedCluster.Spec.DeploymentMode);
        var storedCredentials = SecretStore.GetStored(resourceGroup.KubernetesNamespace, savedCluster.Id);
        Assert.NotNull(storedCredentials);
        Assert.Equal("postgres", storedCredentials!.Username);
        Assert.Equal("postgres-secret", storedCredentials.Password);
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
                FSharpOption<int>.Some(3),
                FSharpOption<int>.Some(25)));

        var updateResult = await Mediator.Send(updateRequest);

        Assert.True(updateResult.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.NotNull(response);
        Assert.Equal(created.Id, response.Id);

        var updatedCluster = await DbContext.Postgres.Find(cluster => cluster.Id == created.Id).FirstOrDefaultAsync();
        Assert.NotNull(updatedCluster);
        Assert.Equal("postgres-main", updatedCluster.Name);
        Assert.Equal(3, updatedCluster.Spec.Instances);
        Assert.Equal(25, updatedCluster.Spec.Storage);
        Assert.Equal(17, updatedCluster.Spec.Version);
        Assert.Equal(PostgresDeploymentMode.Operator, updatedCluster.Spec.DeploymentMode);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("update-postgres-cluster-requested"));
    }

    [Fact]
    public async Task UpdatePassword_RewritesStoredCredentials()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var createResult = await Mediator.Send(TestData.CreatePostgresClusterRequest(resourceGroup.Id));
        Assert.True(createResult.TryGetValue(out var created, out _));

        var updateResult = await Mediator.Send(new UpdatePostgresClusterPasswordRequest(
            created!.Id,
            new UpdatePostgresClusterPasswordRequestBody("rotated-secret")));

        Assert.True(updateResult.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.NotNull(response);

        var storedCredentials = SecretStore.GetStored(resourceGroup.KubernetesNamespace, created.Id);
        Assert.NotNull(storedCredentials);
        Assert.Equal("postgres", storedCredentials!.Username);
        Assert.Equal("rotated-secret", storedCredentials.Password);
    }

    [Fact]
    public async Task GetConnectionString_ReturnsValueBuiltFromStoredCredentials()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var createResult = await Mediator.Send(TestData.CreatePostgresClusterRequest(resourceGroup.Id, name: "postgres-reporting", username: "report_user", password: "report-secret"));
        Assert.True(createResult.TryGetValue(out var created, out _));

        var result = await Mediator.Send(new GetPostgresClusterConnectionStringRequest(created!.Id));

        Assert.True(result.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.NotNull(response);
        Assert.Equal(
            $"Host={created.Id}-rw.{resourceGroup.KubernetesNamespace}.svc.cluster.local;Port=5432;Database={created.Id};Username=report_user;Password=report-secret;SSL Mode=Disable",
            response.ConnectionString);
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
