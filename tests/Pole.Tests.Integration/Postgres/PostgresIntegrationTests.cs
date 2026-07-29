using Microsoft.FSharp.Core;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Postgres.Domain;
using Pole.Features.Postgres.Application.Endpoints;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.Shared.Models;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Postgres;

public sealed class PostgresIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private IMongoCollection<PostgresCredential> Credentials =>
        DbContext.Database.GetCollection<PostgresCredential>(
            PostgresCredential.CollectionName);

    [Fact]
    public async Task Create_PersistsLogicalResourceEncryptedCredentialsAndDeploymentAtomically()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(
            TestData.SeedResourceGroup());

        var result = await Mediator.Send(
            TestData.CreatePostgresClusterRequest(resourceGroup.Id));

        Assert.True(
            result.TryGetValue(out var response, out var error),
            error?.Message);
        var savedCluster = await DbContext.Postgres
            .Find(cluster => cluster.Id == response!.Id)
            .SingleAsync();
        Assert.Equal(resourceGroup.Id, savedCluster.GroupId);
        Assert.Equal(17, savedCluster.Spec.Version);
        Assert.Equal(PostgresDeploymentMode.Operator, savedCluster.Spec.DeploymentMode);
        Assert.Equal(
            TestData.DefaultStorageProfileId,
            savedCluster.Spec.StorageProfileId);
        Assert.Equal(1, savedCluster.Revision);
        Assert.IsType<ActivePostgresClusterLifecycle>(savedCluster.Lifecycle);

        var credential = await Credentials
            .Find(item =>
                item.TenantId == TestData.TenantId &&
                item.PostgresClusterId == savedCluster.Id)
            .SingleAsync();
        Assert.Equal(1, credential.Version);
        Assert.NotEmpty(credential.EncryptedUsername.Ciphertext);
        Assert.NotEmpty(credential.EncryptedPassword.Ciphertext);

        var deployment = await DbContext.ResourceDeployments
            .Find(item =>
                item.TenantId == TestData.TenantId &&
                item.ResourceType ==
                    ResourceDeploymentResourceTypes.PostgreSql.Value &&
                item.ResourceId == savedCluster.Id)
            .SingleAsync();
        Assert.Equal(TestData.DefaultDeploymentTargetId,
            deployment.ResolvedPlacement.DeploymentTargetId);
        Assert.Equal("standard",
            deployment.ResolvedPlacement.Storage!.StorageClassName);
        Assert.Equal(TestData.DefaultStorageProfileId,
            deployment.ResolvedPlacement.Storage.StorageProfileId);
        Assert.Equal(1, deployment.DesiredGeneration);

        var persistedDocuments = await DbContext.Database
            .GetCollection<BsonDocument>(PostgresCredential.CollectionName)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .ToListAsync();
        var outboxDocuments = await DbContext.OutboxMessages
            .Find(_ => true)
            .ToListAsync();
        Assert.DoesNotContain(
            "postgres-secret",
            persistedDocuments.Single().ToJson(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            outboxDocuments,
            message => message.PayloadJson.Contains(
                "postgres-secret",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_ChangesLogicalSpecAndAdvancesDeploymentGeneration()
    {
        var (cluster, deployment) = await CreateClusterAsync();
        var updateRequest = new UpdatePostgresClusterRequest(
            cluster.Id,
            new UpdatePostgresClusterRequestBody(
                FSharpOption<int>.Some(3),
                FSharpOption<int>.Some(25),
                FSharpOption<Resources>.Some(new Resources(
                    Requests: new Resource(2, 512),
                    Limits: new Resource(4, 1024)))),
            TestData.TenantId);

        var result = await Mediator.Send(updateRequest);

        Assert.True(
            result.TryGetValue(out var response, out var error),
            error?.Message);
        Assert.Equal(cluster.Id, response!.Id);
        var updated = await DbContext.Postgres
            .Find(item => item.Id == cluster.Id)
            .SingleAsync();
        Assert.Equal(3, updated.Spec.Instances);
        Assert.Equal(25, updated.Spec.Storage);
        Assert.Equal(2, updated.Revision);
        var updatedDeployment = await DbContext.ResourceDeployments
            .Find(item => item.Id == deployment.Id)
            .SingleAsync();
        Assert.Equal(2, updatedDeployment.DesiredGeneration);
        Assert.Equal(
            deployment.ResolvedPlacement.DeploymentTargetId,
            updatedDeployment.ResolvedPlacement.DeploymentTargetId);
    }

    [Fact]
    public async Task PasswordRotation_ReencryptsCredentialAndAdvancesDeploymentGeneration()
    {
        var (cluster, deployment) = await CreateClusterAsync();
        var before = await Credentials
            .Find(item => item.PostgresClusterId == cluster.Id)
            .SingleAsync();

        var result = await Mediator.Send(
            new UpdatePostgresClusterPasswordRequest(
                cluster.Id,
                new UpdatePostgresClusterPasswordRequestBody("rotated-secret"),
                TestData.TenantId));

        Assert.True(
            result.TryGetValue(out var response, out var error),
            error?.Message);
        Assert.Equal(cluster.Id, response!.Id);
        Assert.Equal(
            deployment.Id,
            response.Placement.DeploymentId);
        Assert.Equal(
            2,
            response.Placement.DesiredGeneration);
        var after = await Credentials
            .Find(item => item.PostgresClusterId == cluster.Id)
            .SingleAsync();
        Assert.Equal(2, after.Version);
        Assert.False(
            before.EncryptedUsername.Ciphertext.SequenceEqual(
                after.EncryptedUsername.Ciphertext));
        Assert.False(
            before.EncryptedPassword.Ciphertext.SequenceEqual(
                after.EncryptedPassword.Ciphertext));
        var updatedDeployment = await DbContext.ResourceDeployments
            .Find(item => item.Id == deployment.Id)
            .SingleAsync();
        Assert.Equal(2, updatedDeployment.DesiredGeneration);

        var rawCredential = await DbContext.Database
            .GetCollection<BsonDocument>(PostgresCredential.CollectionName)
            .Find(Builders<BsonDocument>.Filter.Eq(
                "PostgresClusterId",
                new ObjectId(cluster.Id)))
            .SingleAsync();
        Assert.DoesNotContain(
            "rotated-secret",
            rawCredential.ToJson(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionString_UsesAppliedEndpointAndDecryptedCredential()
    {
        var (cluster, deployment) = await CreateClusterAsync(
            username: "report_user",
            password: "report-secret");
        const string host = "postgres-rw.target-b.svc.cluster.local";
        await MarkAppliedAsync(deployment, host);

        var result = await Mediator.Send(
            new GetPostgresClusterConnectionStringRequest(
                cluster.Id,
                TestData.TenantId));

        Assert.True(
            result.TryGetValue(out var response, out var error),
            error?.Message);
        Assert.Equal(
            $"Host={host};Port=5432;Database={cluster.Id};Username=report_user;Password=report-secret;SSL Mode=Disable",
            response!.ConnectionString);
    }

    [Fact]
    public void ConnectionStringRequest_RequiresTenantWriteAuthorization()
    {
        var request = new GetPostgresClusterConnectionStringRequest(
            ObjectId.GenerateNewId().ToString(),
            TestData.TenantId);

        Assert.IsAssignableFrom<ITenantWriteOperation>(request);
        Assert.IsNotAssignableFrom<ITenantReadOperation>(request);
    }

    [Fact]
    public async Task Delete_MarksLogicalResourceAndRequestsDeploymentDeletion()
    {
        var (cluster, deployment) = await CreateClusterAsync();

        var result = await Mediator.Send(
            new DeletePostgresClusterRequest(cluster.Id, TestData.TenantId));

        Assert.True(
            result.TryGetValue(out _, out var error),
            error?.Message);
        var deleting = await DbContext.Postgres
            .Find(item => item.Id == cluster.Id)
            .SingleAsync();
        Assert.IsType<DeletingPostgresClusterLifecycle>(deleting.Lifecycle);
        Assert.Equal(2, deleting.Revision);
        Assert.Equal(
            ResourceDeploymentOperation.Delete,
            (await DbContext.ResourceDeployments
                .Find(item => item.Id == deployment.Id)
                .SingleAsync()).DesiredOperation);
        Assert.Equal(
            1,
            await Credentials.CountDocumentsAsync(
                item => item.PostgresClusterId == cluster.Id));
    }

    [Fact]
    public async Task Create_ReturnsValidationFailure_WhenResourceRequestsExceedLimits()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(
            TestData.SeedResourceGroup());
        var request = new CreatePostgresClusterRequest(
            new CreatePostgresClusterRequestBody(
                "postgres-invalid-resources",
                resourceGroup.Id,
                17,
                FSharpOption<int>.Some(1),
                FSharpOption<int>.Some(10),
                new Resources(
                    Requests: new Resource(3, 1024),
                    Limits: new Resource(2, 512)),
                TestData.DefaultStorageProfileId,
                "postgres",
                "postgres-secret",
                FSharpOption<PlacementRequest>.None),
            TestData.TenantId);

        var result = await Mediator.Send(request);

        Assert.False(result.TryGetValue(out _, out var error));
        var validation = Assert.IsType<ValidationFailure>(error);
        Assert.Contains(
            validation.ValidationErrors.Keys,
            key => key.StartsWith("Body.Resources", StringComparison.Ordinal));
        Assert.Equal(0, await Credentials.CountDocumentsAsync(_ => true));
        Assert.Equal(
            0,
            await DbContext.ResourceDeployments.CountDocumentsAsync(_ => true));
    }

    private async Task<(PostgresCluster Cluster, ResourceDeployment Deployment)>
        CreateClusterAsync(
            string username = "postgres",
            string password = "postgres-secret")
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(
            TestData.SeedResourceGroup());
        var result = await Mediator.Send(
            TestData.CreatePostgresClusterRequest(
                resourceGroup.Id,
                username: username,
                password: password));
        Assert.True(
            result.TryGetValue(out var response, out var error),
            error?.Message);
        var cluster = await DbContext.Postgres
            .Find(item => item.Id == response!.Id)
            .SingleAsync();
        var deployment = await DbContext.ResourceDeployments
            .Find(item =>
                item.TenantId == TestData.TenantId &&
                item.ResourceType ==
                    ResourceDeploymentResourceTypes.PostgreSql.Value &&
                item.ResourceId == cluster.Id)
            .SingleAsync();
        return (cluster, deployment);
    }

    private async Task MarkAppliedAsync(
        ResourceDeployment deployment,
        string host)
    {
        var startedAt = deployment.UpdatedAtUtc.AddSeconds(1);
        if (deployment.State is WaitingForRuntimeCellResourceDeploymentState)
        {
            var runtimeCell = await DbContext.RuntimeCells
                .Find(item => item.Id == deployment.RuntimeCellId)
                .SingleAsync();
            var readyCell = runtimeCell.MarkReady(
                runtimeCell.DesiredGeneration,
                startedAt);
            await DbContext.RuntimeCells.ReplaceOneAsync(
                item => item.Id == runtimeCell.Id,
                readyCell);
            deployment = deployment.MarkRuntimeCellReady(startedAt);
            startedAt = deployment.UpdatedAtUtc.AddSeconds(1);
        }

        var claimId = ObjectId.GenerateNewId().ToString();
        var claimed = deployment.Start(
            deployment.DesiredGeneration,
            ResourceDeploymentOperation.Apply,
            claimId,
            startedAt,
            startedAt.AddMinutes(1));
        var applied = claimed.Complete(
            deployment.DesiredGeneration,
            ResourceDeploymentOperation.Apply,
            claimId,
            [PostgreSqlResourceDeploymentEndpoint.Create(host, 5432)],
            startedAt.AddSeconds(1));
        await DbContext.ResourceDeployments.ReplaceOneAsync(
            item => item.Id == deployment.Id,
            applied);
    }
}
