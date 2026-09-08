using Microsoft.FSharp.Core;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Domain;
using Pole.Features.Postgres.Application.Endpoints;
using Pole.Features.Postgres.Application.Kubernetes;
using Pole.Features.Postgres.Application.Persistence.Credentials;
using Pole.Features.Postgres.Application.Persistence.Lifecycle;
using Pole.Features.Postgres.Application.Persistence.Mutations;
using Pole.Features.Postgres.Application.Persistence.Reads;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;
using Pole.Features.Postgres.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Features.Postgres.Tests.Integration;

public sealed class PostgresIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private IMongoCollection<PostgresCredential> Credentials =>
        DbContext.Database.GetCollection<PostgresCredential>(
            PostgresCredential.CollectionName);

    [Fact]
    public void FeatureRegistration_ResolvesPostgresCapabilities()
    {
        var catalog = Services.GetRequiredService<Pole.Features.Quotas.Application.Common.QuotaCatalog>();
        Assert.True(catalog.Contains(PostgresClusterCountQuota.QuotaKey));
        Assert.IsType<PostgresQuotaProfile>(catalog.GetProfile<PostgresQuotaInput>());
        _ = Services.GetRequiredService<LoadPostgresClusterById>();
        _ = Services.GetRequiredService<LoadPostgresClusterPage>();
        _ = Services.GetRequiredService<PersistCreatedPostgresCluster>();
        _ = Services.GetRequiredService<PersistUpdatedPostgresCluster>();
        _ = Services.GetRequiredService<BeginPostgresClusterDeletion>();
        _ = Services.GetRequiredService<FinalizePostgresClusterDeletion>();
        _ = Services.GetRequiredService<CreatePostgresCredential>();
        _ = Services.GetRequiredService<LoadPostgresCredential>();
        _ = Services.GetRequiredService<RotatePostgresCredential>();
        _ = Services.GetRequiredService<PersistRotatedPostgresCredential>();
        _ = Services.GetRequiredService<ResolvePostgresClusterCredentials>();
        _ = Services.GetRequiredService<PostgresResourceDeploymentAdapter>();
    }

    [Fact]
    public async Task Create_PersistsLogicalResourceEncryptedCredentialsAndDeploymentAtomically()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(
            PostgresTestData.SeedResourceGroup());

        var result = await Mediator.Send(
            PostgresTestData.CreatePostgresClusterRequest(resourceGroup.Id));

        var response = result.AssertCreated();
        var savedCluster = await DbContext.Postgres
            .Find(cluster => cluster.Id == response.Id)
            .SingleAsync();
        Assert.Equal(resourceGroup.Id, savedCluster.GroupId);
        Assert.Equal(17, savedCluster.Spec.Version);
        Assert.Equal(PostgresDeploymentMode.Operator, savedCluster.Spec.DeploymentMode);
        Assert.Equal(
            PostgresTestData.DefaultStorageProfileId,
            savedCluster.Spec.StorageProfileId);
        Assert.Equal(1, savedCluster.Revision);
        Assert.IsType<ActivePostgresClusterLifecycle>(savedCluster.Lifecycle);

        var credential = await Credentials
            .Find(item =>
                item.TenantId == PostgresTestData.TenantId &&
                item.PostgresClusterId == savedCluster.Id)
            .SingleAsync();
        Assert.Equal(1, credential.Version);
        Assert.NotEmpty(credential.EncryptedUsername.Ciphertext);
        Assert.NotEmpty(credential.EncryptedPassword.Ciphertext);

        var deployment = await DbContext.ResourceDeployments
            .Find(item =>
                item.TenantId == PostgresTestData.TenantId &&
                item.ResourceType ==
                    PostgresResourceDeployment.ResourceType.Value &&
                item.ResourceId == savedCluster.Id)
            .SingleAsync();
        Assert.Equal(PostgresTestData.DefaultDeploymentTargetId,
            deployment.ResolvedPlacement.DeploymentTargetId);
        Assert.Equal("standard",
            deployment.ResolvedPlacement.Storage!.StorageClassName);
        Assert.Equal(PostgresTestData.DefaultStorageProfileId,
            deployment.ResolvedPlacement.Storage.StorageProfileId);
        Assert.Equal(1, deployment.DesiredGeneration);
        var registration = await DbContext.ResourceRegistrations
            .Find(item => item.ResourceId == savedCluster.Id)
            .SingleAsync();
        Assert.Equal(PostgresResourceDeployment.ResourceType.Value, registration.ResourceTypeId);
        Assert.Equal(resourceGroup.Id, registration.ResourceGroupId);

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
                FSharpOption<string>.None),
            PostgresTestData.TenantId);

        var result = await Mediator.Send(updateRequest);

        var response = result.AssertUpdated();
        Assert.Equal(cluster.Id, response.Id);
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
        Assert.Equal(
            deployment.ResolvedPlacement.Compute,
            updatedDeployment.ResolvedPlacement.Compute);
    }

    [Fact]
    public async Task Update_StatefulSetCluster_RejectsMultipleInstances_FromPersistedMode()
    {
        var cluster = PostgresCluster.Create(
            PostgresTestData.TenantId,
            "stateful-postgres",
            ObjectId.GenerateNewId().ToString(),
            17,
            PostgresDeploymentMode.StatefulSet,
            PostgresTestData.DefaultStorageProfileId,
            new CreatePostgresClusterOptions(
                FSharpOption<string>.Some(ObjectId.GenerateNewId().ToString()),
                1,
                20));
        await DbContext.Postgres.InsertOneAsync(cluster);

        var result = await Mediator.Send(new UpdatePostgresClusterRequest(
            cluster.Id,
            new UpdatePostgresClusterRequestBody(
                FSharpOption<int>.Some(2),
                FSharpOption<int>.None,
                FSharpOption<string>.None),
            PostgresTestData.TenantId));

        var outcome = result.AssertSuccess();
        var error = Assert.IsType<UpdatePostgresClusterOutcome.UpdatePostgresClusterRejected>(outcome)
            .Item;
        var state = Assert.IsType<UpdatePostgresClusterError.UpdateState>(error).Item;
        Assert.IsType<UpdatePostgresClusterStateError.PostgresClusterStatefulSetInstancesUnsupported>(state);
        Assert.Equal(
            1,
            (await DbContext.Postgres.Find(item => item.Id == cluster.Id).SingleAsync())
            .Spec.Instances);
    }

    [Fact]
    public async Task Update_WithComputeProfile_AdoptsItsCurrentRevisionAndQuotaRequests()
    {
        var (cluster, deployment) = await CreateClusterAsync();
        var profile = CreateComputeProfile(
            requestedCpuMillis: 600,
            requestedMemoryMi: 700);
        await DbContext.Database
            .GetCollection<ComputeProfile>("ComputeProfiles")
            .InsertOneAsync(profile);

        var result = await Mediator.Send(
            new UpdatePostgresClusterRequest(
                cluster.Id,
                new UpdatePostgresClusterRequestBody(
                    FSharpOption<int>.Some(2),
                    FSharpOption<int>.None,
                    FSharpOption<string>.Some(profile.Id)),
                PostgresTestData.TenantId));

        _ = result.AssertUpdated();
        var updatedDeployment = await DbContext.ResourceDeployments
            .Find(item => item.Id == deployment.Id)
            .SingleAsync();
        Assert.Equal(profile.Id, updatedDeployment.ResolvedPlacement.Compute.ComputeProfileId);
        Assert.Equal(profile.Revision, updatedDeployment.ResolvedPlacement.Compute.ComputeProfileRevision);
        Assert.Equal(600, updatedDeployment.ResolvedPlacement.Compute.Requests.CpuMillis);
        Assert.Equal(700, updatedDeployment.ResolvedPlacement.Compute.Requests.MemoryMi);
    }

    [Fact]
    public async Task Update_WithCurrentComputeProfileId_AdoptsItsLatestRevision()
    {
        var (cluster, deployment) = await CreateClusterAsync();
        var currentProfile = PostgresTestData.SeedComputeProfile();
        var updatedProfile = currentProfile.Reconfigure(
            currentProfile.DisplayName,
            currentProfile.Description,
            ComputeProfileResourceRequirements.Create(
                ComputeProfileResourceQuantity.Create(425, 480),
                currentProfile.Resources.Limits),
            currentProfile.Architecture,
            currentProfile.ExtendedResources,
            currentProfile.AllowedResourceTypes.Select(ResourceTypeId.Create),
            currentProfile.TenantAccess,
            currentProfile.TargetMappings,
            currentProfile.IsEnabled,
            currentProfile.IsDefault,
            currentProfile.UpdatedAtUtc.AddMinutes(1));
        await DbContext.Database
            .GetCollection<ComputeProfile>("ComputeProfiles")
            .ReplaceOneAsync(
                profile => profile.Id == currentProfile.Id,
                updatedProfile);

        var result = await Mediator.Send(
            new UpdatePostgresClusterRequest(
                cluster.Id,
                new UpdatePostgresClusterRequestBody(
                    FSharpOption<int>.None,
                    FSharpOption<int>.None,
                    FSharpOption<string>.Some(currentProfile.Id)),
                PostgresTestData.TenantId));

        _ = result.AssertUpdated();
        var updatedDeployment = await DbContext.ResourceDeployments
            .Find(item => item.Id == deployment.Id)
            .SingleAsync();
        Assert.Equal(updatedProfile.Revision, updatedDeployment.ResolvedPlacement.Compute.ComputeProfileRevision);
        Assert.Equal(425, updatedDeployment.ResolvedPlacement.Compute.Requests.CpuMillis);
        Assert.Equal(480, updatedDeployment.ResolvedPlacement.Compute.Requests.MemoryMi);
    }

    [Fact]
    public async Task Create_RejectsRuntimeClassComputeForCloudNativePgBeforePersistence()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(
            PostgresTestData.SeedResourceGroup());
        var profile = CreateComputeProfile(runtimeClassName: "kata");
        await DbContext.Database
            .GetCollection<ComputeProfile>("ComputeProfiles")
            .InsertOneAsync(profile);
        var request = new CreatePostgresClusterRequest(
            new CreatePostgresClusterRequestBody(
                "postgres-runtime-class",
                resourceGroup.Id,
                17,
                FSharpOption<int>.Some(1),
                FSharpOption<int>.Some(10),
                PostgresTestData.DefaultStorageProfileId,
                FSharpOption<string>.Some(profile.Id),
                "postgres",
                "postgres-secret",
                FSharpOption<PlacementRequest>.None),
            PostgresTestData.TenantId);

        var result = await Mediator.Send(request);

        var outcome = result.AssertSuccess();
        var error = Assert.IsType<CreatePostgresClusterOutcome.CreatePostgresClusterRejected>(outcome)
            .Item;
        _ = Assert.IsType<CreatePostgresClusterError.CreateState>(error).Item;
        Assert.Equal(0, await DbContext.Postgres.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await Credentials.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await DbContext.ResourceDeployments.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task Update_RejectsRuntimeClassComputeForCloudNativePgBeforeMutation()
    {
        var (cluster, deployment) = await CreateClusterAsync();
        var profile = CreateComputeProfile(runtimeClassName: "kata");
        await DbContext.Database
            .GetCollection<ComputeProfile>("ComputeProfiles")
            .InsertOneAsync(profile);

        var result = await Mediator.Send(
            new UpdatePostgresClusterRequest(
                cluster.Id,
                new UpdatePostgresClusterRequestBody(
                    FSharpOption<int>.Some(2),
                    FSharpOption<int>.None,
                    FSharpOption<string>.Some(profile.Id)),
                PostgresTestData.TenantId));

        var outcome = result.AssertSuccess();
        var error = Assert.IsType<UpdatePostgresClusterOutcome.UpdatePostgresClusterRejected>(outcome)
            .Item;
        var state = Assert.IsType<UpdatePostgresClusterError.UpdateState>(error).Item;
        Assert.IsType<UpdatePostgresClusterStateError.PostgresClusterComputeRuntimeClassUnsupported>(state);
        var unchangedCluster = await DbContext.Postgres
            .Find(item => item.Id == cluster.Id)
            .SingleAsync();
        var unchangedDeployment = await DbContext.ResourceDeployments
            .Find(item => item.Id == deployment.Id)
            .SingleAsync();
        Assert.Equal(cluster.Revision, unchangedCluster.Revision);
        Assert.Equal(deployment.Revision, unchangedDeployment.Revision);
        Assert.Equal(
            deployment.ResolvedPlacement.Compute,
            unchangedDeployment.ResolvedPlacement.Compute);
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
                PostgresTestData.TenantId));

        var response = result.AssertPasswordUpdated();
        Assert.Equal(cluster.Id, response.Id);
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
                PostgresTestData.TenantId));

        var response = result.AssertConnectionString();
        Assert.Equal(
            $"Host={host};Port=5432;Database={cluster.Id};Username=report_user;Password=report-secret;SSL Mode=Disable",
            response.ConnectionString);
    }

    [Fact]
    public void ConnectionStringRequest_RequiresTenantWriteAuthorization()
    {
        var request = new GetPostgresClusterConnectionStringRequest(
            ObjectId.GenerateNewId().ToString(),
            PostgresTestData.TenantId);

        Assert.IsAssignableFrom<ITenantWriteOperation>(request);
        Assert.IsNotAssignableFrom<ITenantReadOperation>(request);
    }

    [Fact]
    public async Task Delete_MarksLogicalResourceAndRequestsDeploymentDeletion()
    {
        var (cluster, deployment) = await CreateClusterAsync();

        var result = await Mediator.Send(
            new DeletePostgresClusterRequest(cluster.Id, PostgresTestData.TenantId));

        _ = result.AssertDeletionAccepted();
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
    public async Task FinalizeDeletion_IsTenantScoped_AtomicAndIdempotent()
    {
        var (cluster, _) = await CreateClusterAsync();
        _ = (await Mediator.Send(
            new DeletePostgresClusterRequest(cluster.Id, PostgresTestData.TenantId)))
            .AssertDeletionAccepted();

        var deployment = await DbContext.ResourceDeployments
            .Find(candidate =>
                candidate.TenantId == PostgresTestData.TenantId &&
                candidate.ResourceType ==
                    PostgresResourceDeployment.ResourceType.Value &&
                candidate.ResourceId == cluster.Id)
            .SingleAsync();
        var startedAtUtc = deployment.UpdatedAtUtc.AddSeconds(1);
        var claimId = ObjectId.GenerateNewId().ToString();
        var deletedDeployment = deployment
            .Start(
                deployment.DesiredGeneration,
                ResourceDeploymentOperation.Delete,
                claimId,
                startedAtUtc,
                startedAtUtc.AddMinutes(1))
            .Complete(
                deployment.DesiredGeneration,
                ResourceDeploymentOperation.Delete,
                claimId,
                [],
                startedAtUtc.AddSeconds(1));
        await DbContext.ResourceDeployments.ReplaceOneAsync(
            candidate => candidate.Id == deployment.Id,
            deletedDeployment);

        var finalize = Services.GetRequiredService<FinalizePostgresClusterDeletion>();
        var foreignTenantResult = await finalize.Invoke(
            ObjectId.GenerateNewId().ToString(),
            cluster.Id,
            CancellationToken.None);

        Assert.True(foreignTenantResult.IsPostgresSuccess);
        Assert.Equal(1L, await DbContext.Postgres.CountDocumentsAsync(
            candidate => candidate.Id == cluster.Id));
        Assert.Equal(1L, await Credentials.CountDocumentsAsync(
            candidate => candidate.PostgresClusterId == cluster.Id));
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync(
            "pole.postgres/v1/postgres-cluster-deleted"));

        var result = await finalize.Invoke(
            PostgresTestData.TenantId,
            cluster.Id,
            CancellationToken.None);
        var repeated = await finalize.Invoke(
            PostgresTestData.TenantId,
            cluster.Id,
            CancellationToken.None);

        Assert.True(result.IsPostgresSuccess);
        Assert.True(repeated.IsPostgresSuccess);
        Assert.Equal(0L, await DbContext.Postgres.CountDocumentsAsync(
            candidate => candidate.Id == cluster.Id));
        Assert.Equal(0L, await Credentials.CountDocumentsAsync(
            candidate => candidate.PostgresClusterId == cluster.Id));
        Assert.Equal(0L, await DbContext.ResourceDeployments.CountDocumentsAsync(
            candidate =>
                candidate.TenantId == PostgresTestData.TenantId &&
                candidate.ResourceType ==
                    PostgresResourceDeployment.ResourceType.Value &&
                    candidate.ResourceId == cluster.Id));
        Assert.Equal(0L, await DbContext.ResourceRegistrations.CountDocumentsAsync(
            registration => registration.ResourceId == cluster.Id));
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(
            "pole.postgres/v1/postgres-cluster-deleted"));
    }

    [Fact]
    public async Task Create_ReturnsValidationFailure_WhenComputeProfileIdIsMalformed()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(
            PostgresTestData.SeedResourceGroup());
        var request = new CreatePostgresClusterRequest(
            new CreatePostgresClusterRequestBody(
                "postgres-invalid-resources",
                resourceGroup.Id,
                17,
                FSharpOption<int>.Some(1),
                FSharpOption<int>.Some(10),
                PostgresTestData.DefaultStorageProfileId,
                FSharpOption<string>.Some("not-an-object-id"),
                "postgres",
                "postgres-secret",
                FSharpOption<PlacementRequest>.None),
            PostgresTestData.TenantId);

        var result = await Mediator.Send(request);

        var validation = result.AssertValidationRejected();
        Assert.Contains(
            validation.Errors.Keys,
            key => key.StartsWith("Body.ComputeProfileId", StringComparison.Ordinal));
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
            PostgresTestData.SeedResourceGroup());
        var result = await Mediator.Send(
            PostgresTestData.CreatePostgresClusterRequest(
                resourceGroup.Id,
                username: username,
                password: password));
        var response = result.AssertCreated();
        var cluster = await DbContext.Postgres
            .Find(item => item.Id == response.Id)
            .SingleAsync();
        var deployment = await DbContext.ResourceDeployments
            .Find(item =>
                item.TenantId == PostgresTestData.TenantId &&
                item.ResourceType ==
                    PostgresResourceDeployment.ResourceType.Value &&
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
            [ResourceDeploymentEndpoint.Create("PostgreSql", host, 5432, "postgresql")],
            startedAt.AddSeconds(1));
        await DbContext.ResourceDeployments.ReplaceOneAsync(
            item => item.Id == deployment.Id,
            applied);
    }

    private static ComputeProfile CreateComputeProfile(
        long requestedCpuMillis = 500,
        long requestedMemoryMi = 512,
        string? runtimeClassName = null) =>
        ComputeProfileFactory.Create(
            "PostgreSQL compute",
            "PostgreSQL integration-test profile",
            ComputeProfileResourceRequirements.Create(
                ComputeProfileResourceQuantity.Create(
                    requestedCpuMillis,
                    requestedMemoryMi),
                ComputeProfileResourceQuantity.Create(2000, 2048)),
            ComputeArchitecture.Any,
            [],
            [PostgresResourceDeployment.ResourceType],
            AllTenantsComputeProfileAccess.Create(),
            [
                ComputeProfileTargetMapping.Create(
                    PostgresTestData.DefaultDeploymentTargetId,
                    runtimeClassName: runtimeClassName)
            ],
            isEnabled: true,
            isDefault: false,
            new ComputeProfileFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
}
