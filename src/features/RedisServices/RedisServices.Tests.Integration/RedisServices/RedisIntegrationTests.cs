using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.Quotas.Contracts;
using Pole.Features.RedisServices.Application;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Declarations;
using Pole.Features.RedisServices.Application.Endpoints;
using Pole.Features.RedisServices.Application.Kubernetes;
using Pole.Features.RedisServices.Application.Lifecycle;
using Pole.Features.RedisServices.Application.Mongo;
using Pole.Features.RedisServices.Application.Services;
using Pole.Features.RedisServices.Domain;
using Pole.Features.RedisServices.Tests.Integration.Infrastructure;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.StorageProfiles.Contracts;
using Pole.ResultType;
using Xunit;

namespace Pole.Features.RedisServices.Tests.Integration.RedisServices;

public sealed class RedisIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private const string OtherTenantId = "507f1f77bcf86cd799439010";

    [Fact]
    public void FeatureRegistration_ResolvesRedisCapabilities()
    {
        var catalog = Services.GetRequiredService<QuotaCatalog>();
        Assert.True(catalog.Contains(CpuQuota.QuotaKey));
        Assert.True(catalog.Contains(RedisServiceCountQuota.QuotaKey));
        Assert.DoesNotContain(catalog.Definitions, quota => quota.Key.Value == "WebServiceCount");
        Assert.IsType<RedisQuotaProfile>(catalog.GetProfile<RedisQuotaInput>());
        _ = Services.GetRequiredService<LoadRedisById>();
        _ = Services.GetRequiredService<LoadRedisPage>();
        _ = Services.GetRequiredService<PersistCreatedRedis>();
        _ = Services.GetRequiredService<PersistUpdatedRedis>();
        _ = Services.GetRequiredService<BeginRedisDeletion>();
        _ = Services.GetRequiredService<FinalizeRedisDeletion>();
        _ = Services.GetRequiredService<RedisResourceDeploymentAdapter>();
        _ = Services.GetRequiredService<ResolveStorageProfile>();
        _ = Services.GetRequiredService<AdmitDeploymentTargetForNewWorkload>();
    }

    [Fact]
    public async Task LoadById_RejectsMalformedStructuralIdentity()
    {
        var loadRedisById = Services.GetRequiredService<LoadRedisById>();

        await Assert.ThrowsAsync<ArgumentException>(() => loadRedisById(
            "not-a-tenant-id",
            ObjectId.GenerateNewId().ToString(),
            CancellationToken.None));
    }

    [Fact]
    public async Task PersistUpdate_RejectsADeletingReplacement()
    {
        var current = await DbContext.InsertRedisAsync(
            SeedRedis(RedisTestData.TenantId, "invalid-update-transition"));
        var replacement = current.BeginDeletion(DateTime.UtcNow);
        var persistUpdatedRedis = Services.GetRequiredService<PersistUpdatedRedis>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DbContext.RunOutcomeInTransactionAsync<Unit>(async (transaction, cancellationToken) =>
            {
                await persistUpdatedRedis(
                    new(current, replacement, null),
                    transaction.Session,
                    cancellationToken);
                return transaction.Commit(Unit.Value);
            }));
    }

    [Fact]
    public async Task PersistUpdate_RejectsRename()
    {
        var current = await DbContext.InsertRedisAsync(
            SeedRedis(RedisTestData.TenantId, "immutable-name"));
        var replacement = current with { Name = "renamed" };
        var persistUpdatedRedis = Services.GetRequiredService<PersistUpdatedRedis>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DbContext.RunOutcomeInTransactionAsync<Unit>(async (transaction, cancellationToken) =>
            {
                await persistUpdatedRedis(
                    new(current, replacement, null),
                    transaction.Session,
                    cancellationToken);
                return transaction.Commit(Unit.Value);
            }));
    }

    [Fact]
    public async Task Create_Succeeds_WithValidRequest()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(RedisTestData.SeedResourceGroup());

        var result = await Mediator.Send(RedisTestData.CreateRedisServiceRequest(resourceGroup.Id));

        var response = result.AssertSuccess();

        var savedRedis = await DbContext.RedisServices.Find(redis => redis.Id == response.RedisId).FirstOrDefaultAsync();
        Assert.NotNull(savedRedis);
        var deployment = await DbContext.ResourceDeployments
            .Find(candidate => candidate.ResourceId == response.RedisId)
            .SingleAsync();
        var defaultProfile = await DbContext.Database
            .GetCollection<ComputeProfile>("ComputeProfiles")
            .Find(profile => profile.IsDefault)
            .SingleAsync();
        Assert.Equal(
            defaultProfile.Id,
            deployment.ResolvedPlacement.Compute.ComputeProfileId);
        Assert.Equal(
            defaultProfile.Revision,
            deployment.ResolvedPlacement.Compute.ComputeProfileRevision);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("pole.redis/v1/redis-service-created"));
        var outbox = await DbContext.OutboxMessages
            .Find(message => message.EventType == "pole.redis/v1/redis-service-created")
            .SingleAsync();
        Assert.Equal(RedisTestData.TenantId, outbox.TenantId);
        var registration = await DbContext.ResourceRegistrations
            .Find(item => item.ResourceId == savedRedis.Id)
            .SingleAsync();
        Assert.Equal(RedisResourceDeployment.ResourceType.Value, registration.ResourceTypeId);
        Assert.Equal(resourceGroup.Id, registration.ResourceGroupId);
    }

    [Fact]
    public async Task Create_RejectsDuplicateNameWithinTenant()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(RedisTestData.SeedResourceGroup());
        var request = RedisTestData.CreateRedisServiceRequest(resourceGroup.Id, "orders-cache");

        _ = (await Mediator.Send(request)).AssertSuccess();
        var duplicate = await Mediator.Send(request);

        Assert.IsType<RedisServiceNameAlreadyExists>(
            ResultAssertionExtensions.AssertSuccess<CreateRedisServiceOutcome>(duplicate).Value);
    }

    [Fact]
    public async Task PersistCreate_DoesNotReportIdCollisionAsNameCollision()
    {
        var existing = await DbContext.InsertRedisAsync(
            SeedRedis(RedisTestData.TenantId, "existing-id"));
        var collidingId = SeedRedis(RedisTestData.TenantId, "different-name") with
        {
            Id = existing.Id
        };
        var persistCreatedRedis = Services.GetRequiredService<PersistCreatedRedis>();

        var exception = await Assert.ThrowsAsync<MongoWriteException>(() =>
            DbContext.RunOutcomeInTransactionAsync<Unit>(async (transaction, cancellationToken) =>
            {
                await persistCreatedRedis(
                    transaction.Session,
                    collidingId,
                    cancellationToken);
                return transaction.Commit(Unit.Value);
            }));

        Assert.Equal(ServerErrorCategory.DuplicateKey, exception.WriteError.Category);
    }

    [Fact]
    public async Task NameIndexAllowsSameNameInAnotherTenantAndAnotherKind()
    {
        var first = SeedRedis(RedisTestData.TenantId, "shared-name");
        var otherTenant = SeedRedis(OtherTenantId, "shared-name");
        await DbContext.RedisServices.InsertOneAsync(first);
        await DbContext.RedisServices.InsertOneAsync(otherTenant);

        var otherKind = DbContext.Database.GetCollection<BsonDocument>("Postgres");
        await otherKind.InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["TenantId"] = RedisTestData.TenantId,
            ["Name"] = "shared-name"
        });

        Assert.Equal(2L, await DbContext.RedisServices.CountDocumentsAsync(
            item => item.Name == "shared-name"));
        Assert.Equal(1L, await otherKind.CountDocumentsAsync(new BsonDocument("Name", "shared-name")));
    }

    [Fact]
    public async Task Create_ReturnsValidationFailure_ForInvalidName()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(RedisTestData.SeedResourceGroup());
        var request = RedisTestData.CreateRedisServiceRequest(resourceGroup.Id, "Bad Name");

        var result = await Mediator.Send(request);

        var validation = result.AssertValidationRejected();
        Assert.Contains("Body.Name", validation.Errors.Keys);
    }

    [Fact]
    public async Task Create_ReturnsValidationFailure_ForInvalidComputeProfileId()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(
            RedisTestData.SeedResourceGroup());
        var validRequest = RedisTestData.CreateRedisServiceRequest(resourceGroup.Id);
        var request = validRequest with
        {
            Body = validRequest.Body with
            {
                ComputeProfileId = "not-an-object-id"
            }
        };

        var result = await Mediator.Send(request);

        var validation = result.AssertValidationRejected();
        Assert.Contains(
            "Body.ComputeProfileId",
            validation.Errors.Keys);
    }

    [Fact]
    public async Task PersistCreate_AbortsRedisAndOutbox_WhenTransactionFails()
    {
        var redis = SeedRedis(RedisTestData.TenantId, "aborted-create");
        var persistCreatedRedis = Services.GetRequiredService<PersistCreatedRedis>();

        var result = await DbContext.RunOutcomeInTransactionAsync<TransactionAborted>(async (
            transaction,
            cancellationToken) =>
        {
            await persistCreatedRedis(transaction.Session, redis, cancellationToken);
            return transaction.Abort(new TransactionAborted("Abort the containing workflow."));
        });

        Assert.Equal("Abort the containing workflow.", result.Message);
        Assert.Equal(0L, await DbContext.RedisServices.CountDocumentsAsync(
            candidate => candidate.Id == redis.Id));
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync(
            "pole.redis/v1/redis-service-created"));
    }

    [Fact]
    public async Task Update_ChangesTagsWithoutResizingStorage()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(RedisTestData.SeedResourceGroup());
        var createResult = await Mediator.Send(RedisTestData.CreateRedisServiceRequest(resourceGroup.Id));
        var created = createResult.AssertSuccess();

        var updateRequest = new UpdateRedisServiceRequest(
            Id: created.RedisId,
            Body: new UpdateRedisServiceRequestBody(
                Spec: new(
                    Storage: new(5, RedisTestData.DefaultStorageProfileId)),
                Tags: ["cache", "prod"]),
            TenantId: RedisTestData.TenantId);

        var updateResult = await Mediator.Send(updateRequest);

        _ = updateResult.AssertSuccess();

        var updated = await DbContext.RedisServices.Find(redis => redis.Id == created.RedisId).FirstOrDefaultAsync();
        Assert.NotNull(updated);
        Assert.Equal(5, updated.Spec.Storage.SizeGb);
        Assert.Equal(RedisTestData.DefaultStorageProfileId, updated.Spec.Storage.StorageProfileId);
        Assert.Equal(["cache", "prod"], updated.Tags.Order());
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("pole.redis/v1/redis-service-updated"));
        var outbox = await DbContext.OutboxMessages
            .Find(message => message.EventType == "pole.redis/v1/redis-service-updated")
            .SingleAsync();
        Assert.Equal(RedisTestData.TenantId, outbox.TenantId);
    }

    [Fact]
    public async Task Update_WithoutComputeProfileId_PreservesExistingSnapshot()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(
            RedisTestData.SeedResourceGroup());
        var createResult = await Mediator.Send(
            RedisTestData.CreateRedisServiceRequest(resourceGroup.Id));
        var created = createResult.AssertSuccess();
        var originalDeployment = await LoadDeploymentAsync(created.RedisId);
        await ReconfigureDefaultComputeProfileAsync(
            requestsCpuMillis: 500,
            requestsMemoryMi: 512);

        var updateResult = await Mediator.Send(new UpdateRedisServiceRequest(
            created.RedisId,
            new UpdateRedisServiceRequestBody(
                new RedisSpecDto(new StorageDto(
                    5,
                    RedisTestData.DefaultStorageProfileId)),
                ImmutableHashSet.Create("updated")),
            RedisTestData.TenantId));

        updateResult.AssertSuccess();
        var updatedDeployment = await LoadDeploymentAsync(created.RedisId);
        Assert.Equal(
            originalDeployment.ResolvedPlacement.Compute.ToBsonDocument(),
            updatedDeployment.ResolvedPlacement.Compute.ToBsonDocument());
    }

    [Fact]
    public async Task Update_WithComputeProfileId_AdoptsLatestProfileRevision()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(
            RedisTestData.SeedResourceGroup());
        var createResult = await Mediator.Send(
            RedisTestData.CreateRedisServiceRequest(resourceGroup.Id));
        var created = createResult.AssertSuccess();
        var originalDeployment = await LoadDeploymentAsync(created.RedisId);
        var updatedProfile = await ReconfigureDefaultComputeProfileAsync(
            requestsCpuMillis: 500,
            requestsMemoryMi: 512);

        var updateResult = await Mediator.Send(new UpdateRedisServiceRequest(
            created.RedisId,
            new UpdateRedisServiceRequestBody(
                new RedisSpecDto(new StorageDto(
                    5,
                    RedisTestData.DefaultStorageProfileId)),
                ImmutableHashSet.Create("updated"),
                updatedProfile.Id),
            RedisTestData.TenantId));

        updateResult.AssertSuccess();
        var updatedDeployment = await LoadDeploymentAsync(created.RedisId);
        Assert.Equal(
            originalDeployment.ResolvedPlacement.Compute.ComputeProfileId,
            updatedDeployment.ResolvedPlacement.Compute.ComputeProfileId);
        Assert.Equal(
            updatedProfile.Revision,
            updatedDeployment.ResolvedPlacement.Compute.ComputeProfileRevision);
        Assert.Equal(
            500,
            updatedDeployment.ResolvedPlacement.Compute.Requests.CpuMillis);
        Assert.Equal(
            512,
            updatedDeployment.ResolvedPlacement.Compute.Requests.MemoryMi);
    }

    [Fact]
    public async Task DisabledProfile_BlocksAdoption_ButOmittedUpdatePreservesSnapshot()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(
            RedisTestData.SeedResourceGroup());
        var profile = RedisTestData.SeedComputeProfile(
            ObjectId.GenerateNewId().ToString(),
            requestedCpuMillis: 400,
            requestedMemoryMi: 450,
            isDefault: false);
        var profiles = DbContext.Database
            .GetCollection<ComputeProfile>("ComputeProfiles");
        await profiles.InsertOneAsync(profile);
        var defaultRequest = RedisTestData.CreateRedisServiceRequest(resourceGroup.Id);
        var createRequest = defaultRequest with
        {
            Body = defaultRequest.Body with { ComputeProfileId = profile.Id }
        };
        var created = (await Mediator.Send(createRequest)).AssertSuccess();
        var originalDeployment = await LoadDeploymentAsync(created.RedisId);

        var disabled = profile.Reconfigure(
            profile.DisplayName,
            profile.Description,
            profile.Resources,
            profile.Architecture,
            profile.ExtendedResources,
            profile.AllowedResourceTypes.Select(ResourceTypeId.Create),
            profile.TenantAccess,
            profile.TargetMappings,
            isEnabled: false,
            isDefault: false,
            profile.UpdatedAtUtc.AddMinutes(1));
        await profiles.ReplaceOneAsync(candidate => candidate.Id == profile.Id, disabled);

        (await Mediator.Send(UpdateRequest(created.RedisId, RedisTestData.TenantId)))
            .AssertSuccess();
        var preservedDeployment = await LoadDeploymentAsync(created.RedisId);
        Assert.Equal(
            originalDeployment.ResolvedPlacement.Compute.ToBsonDocument(),
            preservedDeployment.ResolvedPlacement.Compute.ToBsonDocument());

        var explicitAdoption = await Mediator.Send(new UpdateRedisServiceRequest(
            created.RedisId,
            new UpdateRedisServiceRequestBody(
                RedisTestData.ValidRedisSpec(),
                ImmutableHashSet.Create("updated"),
                profile.Id),
            RedisTestData.TenantId));
        Assert.IsType<ComputeProfileResolutionError>(
            ResultAssertionExtensions.AssertSuccess<UpdateRedisServiceOutcome>(explicitAdoption).Value);
    }

    [Fact]
    public async Task Update_ReturnsTypedError_WhenStorageProfileChanges()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(RedisTestData.SeedResourceGroup());
        var createResult = await Mediator.Send(RedisTestData.CreateRedisServiceRequest(resourceGroup.Id));
        var created = createResult.AssertSuccess();
        var otherProfileId = ObjectId.GenerateNewId().ToString();
        await DbContext.StorageProfiles.InsertOneAsync(RedisTestData.SeedStorageProfile(
            id: otherProfileId,
            storageClassName: "fast",
            isDefault: false));

        var updateRequest = new UpdateRedisServiceRequest(
            Id: created.RedisId,
            Body: new UpdateRedisServiceRequestBody(
                Spec: new(
                    Storage: new(5, otherProfileId)),
                Tags: []),
            TenantId: RedisTestData.TenantId);

        var updateResult = await Mediator.Send(updateRequest);

        Assert.IsType<RedisStorageProfileChangeNotAllowed>(
            ResultAssertionExtensions.AssertSuccess<UpdateRedisServiceOutcome>(updateResult).Value);
    }

    [Fact]
    public async Task GetList_AppliesPagination()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(RedisTestData.SeedResourceGroup());

        await Mediator.Send(RedisTestData.CreateRedisServiceRequest(resourceGroup.Id, "redis-alpha"));
        await Mediator.Send(RedisTestData.CreateRedisServiceRequest(resourceGroup.Id, "redis-beta"));

        var result = await Mediator.Send(new GetRedisServicesRequest
        {
            TenantId = RedisTestData.TenantId,
            PageNumber = 2,
            PageSize = 1
        });

        var response = result.AssertSuccess();
        Assert.Single(response.Items);
        Assert.Equal(2L, response.TotalCount);
        Assert.Equal(2, response.PageNumber);
        Assert.Equal(1, response.PageSize);
    }

    [Fact]
    public async Task GetById_ReturnsNotFound_ForAnotherTenantsRedis()
    {
        var foreignRedis = await DbContext.InsertRedisAsync(
            SeedRedis(OtherTenantId, "foreign-detail"));

        var result = await Mediator.Send(new GetRedisServiceByIdRequest(
            foreignRedis.Id,
            RedisTestData.TenantId));

        var notFound = ResultAssertionExtensions
            .AssertSuccess<GetRedisServiceByIdOutcome>(result)
            .AssertFailure();
        Assert.Equal(foreignRedis.Id, notFound.RedisId);
    }

    [Fact]
    public async Task GetList_ReturnsOnlySelectedTenantsRedis()
    {
        var ownRedis = await DbContext.InsertRedisAsync(
            SeedRedis(RedisTestData.TenantId, "own-list"));
        await DbContext.InsertRedisAsync(
            SeedRedis(OtherTenantId, "foreign-list"));

        var result = await Mediator.Send(new GetRedisServicesRequest
        {
            TenantId = RedisTestData.TenantId,
            PageNumber = 1,
            PageSize = 10
        });

        var response = result.AssertSuccess();
        var item = Assert.Single(response.Items);
        Assert.Equal(ownRedis.Id, item.Id);
        Assert.Equal(1L, response.TotalCount);
    }

    [Fact]
    public async Task Update_ReturnsNotFound_ForAnotherTenantsRedis()
    {
        var foreignRedis = await DbContext.InsertRedisAsync(
            SeedRedis(OtherTenantId, "foreign-update"));
        var request = UpdateRequest(foreignRedis.Id, RedisTestData.TenantId);

        var result = await Mediator.Send(request);

        Assert.IsType<RedisServiceNotFound>(
            ResultAssertionExtensions.AssertSuccess<UpdateRedisServiceOutcome>(result).Value);
        var stored = await DbContext.RedisServices
            .Find(redis => redis.Id == foreignRedis.Id)
            .SingleAsync();
        Assert.Equal(foreignRedis.Revision, stored.Revision);
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync(
            "pole.redis/v1/redis-service-updated"));
    }

    [Fact]
    public async Task PersistUpdate_ReturnsConflict_ForStaleRevision()
    {
        var current = await DbContext.InsertRedisAsync(
            SeedRedis(RedisTestData.TenantId, "stale-update"));
        var concurrent = current.UpdateDesiredState(
            current.Spec,
            ImmutableHashSet.Create("concurrent"));
        await DbContext.RedisServices.ReplaceOneAsync(
            redis => redis.Id == current.Id,
            concurrent);
        var intended = current.UpdateDesiredState(
            current.Spec,
            ImmutableHashSet.Create("intended"));
        var persistUpdatedRedis = Services.GetRequiredService<PersistUpdatedRedis>();

        var result = await DbContext.RunOutcomeInTransactionAsync<
            PersistUpdatedRedisOutcome>(async (transaction, cancellationToken) =>
        {
            var outcome = await persistUpdatedRedis(
                new(current, intended, null),
                    transaction.Session,
                cancellationToken);
            return outcome switch
            {
                RedisUpdated success => transaction.Commit(success),
                RedisUpdateConflict error => transaction.Abort(error),
                RedisGitOwnedConfiguration error => transaction.Abort(error)
            };
        });

        Assert.Equal(current.Id, Assert.IsType<RedisUpdateConflict>(result.Value).RedisId);
        var stored = await DbContext.RedisServices
            .Find(redis => redis.Id == current.Id)
            .SingleAsync();
        Assert.Equal(ImmutableHashSet.Create("concurrent"), stored.Tags);
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync(
            "pole.redis/v1/redis-service-updated"));
    }

    [Fact]
    public async Task BeginDeletion_ReturnsConflict_ForStaleRevision()
    {
        var current = await DbContext.InsertRedisAsync(
            SeedRedis(RedisTestData.TenantId, "stale-delete"));
        var concurrent = current.UpdateDesiredState(
            current.Spec,
            ImmutableHashSet.Create("concurrent"));
        await DbContext.RedisServices.ReplaceOneAsync(
            redis => redis.Id == current.Id,
            concurrent);
        var deleting = current.BeginDeletion(DateTime.UtcNow);
        var beginRedisDeletion = Services.GetRequiredService<BeginRedisDeletion>();

        var result = await DbContext.RunOutcomeInTransactionAsync<
            BeginRedisDeletionOutcome>(async (transaction, cancellationToken) =>
        {
            var outcome = await beginRedisDeletion(
                new(current, deleting),
                transaction.Session,
                cancellationToken);
            return outcome switch
            {
                RedisDeletionStarted success => transaction.Commit(success),
                RedisSecretBindingRemovalRequired error => transaction.Abort(error),
                RedisDeletionConflict error => transaction.Abort(error),
                RedisGitOwnedConfiguration error => transaction.Abort(error)
            };
        });

        Assert.Equal(current.Id, Assert.IsType<RedisDeletionConflict>(result.Value).RedisId);
        var stored = await DbContext.RedisServices
            .Find(redis => redis.Id == current.Id)
            .SingleAsync();
        Assert.IsType<ActiveRedisLifecycle>(stored.Lifecycle);
        Assert.Equal(ImmutableHashSet.Create("concurrent"), stored.Tags);
    }

    [Fact]
    public async Task Delete_MarksRedisDeleting_AndRequestsDeploymentDeletion()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(RedisTestData.SeedResourceGroup());
        var createResult = await Mediator.Send(RedisTestData.CreateRedisServiceRequest(resourceGroup.Id));
        var created = createResult.AssertSuccess();

        var deleteResult = await Mediator.Send(new DeleteRedisServiceRequest(created.RedisId, RedisTestData.TenantId));

        var response = deleteResult.AssertSuccess();
        Assert.Equal(created.RedisId, response.Id);

        var deleting = await DbContext.RedisServices
            .Find(redis => redis.Id == created.RedisId)
            .SingleAsync();
        Assert.IsType<Pole.Features.RedisServices.Domain.DeletingRedisLifecycle>(
            deleting.Lifecycle);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(
            "pole.resource-deployments/v1/resource-deployment-deletion-requested"));
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync(
            "pole.redis/v1/redis-service-deleted"));
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_WhenRedisDoesNotExist()
    {
        var result = await Mediator.Send(new DeleteRedisServiceRequest("507f1f77bcf86cd799439012", RedisTestData.TenantId));

        Assert.IsType<RedisServiceNotFound>(
            ResultAssertionExtensions.AssertSuccess<DeleteRedisServiceOutcome>(result).Value);
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_ForAnotherTenantsRedis()
    {
        var foreignRedis = await DbContext.InsertRedisAsync(
            SeedRedis(OtherTenantId, "foreign-delete"));

        var result = await Mediator.Send(new DeleteRedisServiceRequest(
            foreignRedis.Id,
            RedisTestData.TenantId));

        Assert.IsType<RedisServiceNotFound>(
            ResultAssertionExtensions.AssertSuccess<DeleteRedisServiceOutcome>(result).Value);
        var stored = await DbContext.RedisServices
            .Find(redis => redis.Id == foreignRedis.Id)
            .SingleAsync();
        Assert.IsType<ActiveRedisLifecycle>(stored.Lifecycle);
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync(
            "pole.resource-deployments/v1/resource-deployment-deletion-requested"));
    }

    [Fact]
    public async Task FinalizeDeletion_IsTenantScoped_AtomicAndIdempotent()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(
            RedisTestData.SeedResourceGroup());
        var createResult = await Mediator.Send(
            RedisTestData.CreateRedisServiceRequest(resourceGroup.Id, "finalize-redis"));
        var created = createResult.AssertSuccess();
        var deleteResult = await Mediator.Send(new DeleteRedisServiceRequest(
            created.RedisId,
            RedisTestData.TenantId));
        _ = deleteResult.AssertSuccess();

        var deployment = await DbContext.ResourceDeployments
            .Find(candidate =>
                candidate.TenantId == RedisTestData.TenantId &&
                candidate.ResourceType == RedisResourceDeployment.ResourceType.Value &&
                candidate.ResourceId == created.RedisId)
            .SingleAsync();
        var startedAtUtc = deployment.UpdatedAtUtc.AddSeconds(1);
        var claimId = ObjectId.GenerateNewId().ToString();
        var reconciling = deployment.Start(
            deployment.DesiredGeneration,
            ResourceDeploymentOperation.Delete,
            claimId,
            startedAtUtc,
            startedAtUtc.AddMinutes(1));
        var deletedDeployment = reconciling.Complete(
            deployment.DesiredGeneration,
            ResourceDeploymentOperation.Delete,
            claimId,
            [],
            startedAtUtc.AddSeconds(1));
        await DbContext.ResourceDeployments.ReplaceOneAsync(
            candidate => candidate.Id == deployment.Id,
            deletedDeployment);
        var finalizeRedisDeletion = Services.GetRequiredService<FinalizeRedisDeletion>();

        var wrongTenantResult = await finalizeRedisDeletion(
            OtherTenantId,
            created.RedisId,
            CancellationToken.None);

        Assert.IsType<RedisDeletionFinalized>(wrongTenantResult.Value);
        Assert.Equal(1L, await DbContext.RedisServices.CountDocumentsAsync(
            redis => redis.Id == created.RedisId));
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync(
            "pole.redis/v1/redis-service-deleted"));

        var result = await finalizeRedisDeletion(
            RedisTestData.TenantId,
            created.RedisId,
            CancellationToken.None);
        var repeatedResult = await finalizeRedisDeletion(
            RedisTestData.TenantId,
            created.RedisId,
            CancellationToken.None);

        Assert.IsType<RedisDeletionFinalized>(result.Value);
        Assert.IsType<RedisDeletionFinalized>(repeatedResult.Value);
        Assert.Equal(0L, await DbContext.RedisServices.CountDocumentsAsync(
            redis => redis.Id == created.RedisId));
        Assert.Equal(0L, await DbContext.ResourceDeployments.CountDocumentsAsync(
            candidate =>
                candidate.TenantId == RedisTestData.TenantId &&
                candidate.ResourceType == RedisResourceDeployment.ResourceType.Value &&
                candidate.ResourceId == created.RedisId));
        Assert.Equal(0L, await DbContext.ResourceRegistrations.CountDocumentsAsync(
            registration => registration.ResourceId == created.RedisId));
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(
            "pole.redis/v1/redis-service-deleted"));
        var outbox = await DbContext.OutboxMessages
            .Find(message => message.EventType == "pole.redis/v1/redis-service-deleted")
            .SingleAsync();
        Assert.Equal(RedisTestData.TenantId, outbox.TenantId);
    }

    private static Redis SeedRedis(
        string tenantId,
        string name) =>
        RedisFactory.Create(
            tenantId,
            name,
            ObjectId.GenerateNewId().ToString(),
            RedisTestData.ValidRedisSpec().ToDomain(),
            RedisFactory.DefaultCreateOptions);

    private sealed record TransactionAborted(string Message);

    private static UpdateRedisServiceRequest UpdateRequest(
        string redisId,
        string tenantId) =>
        new(
            redisId,
            new UpdateRedisServiceRequestBody(
                RedisTestData.ValidRedisSpec(),
                ImmutableHashSet.Create("updated")),
            tenantId);

    private Task<ResourceDeployment> LoadDeploymentAsync(string redisId) =>
        DbContext.ResourceDeployments
            .Find(candidate =>
                candidate.TenantId == RedisTestData.TenantId &&
                candidate.ResourceType == RedisResourceDeployment.ResourceType.Value &&
                candidate.ResourceId == redisId)
            .SingleAsync();

    private async Task<ComputeProfile> ReconfigureDefaultComputeProfileAsync(
        long requestsCpuMillis,
        long requestsMemoryMi)
    {
        var profiles = DbContext.Database
            .GetCollection<ComputeProfile>("ComputeProfiles");
        var current = await profiles
            .Find(profile => profile.IsDefault)
            .SingleAsync();
        var replacement = current.Reconfigure(
            current.DisplayName,
            current.Description,
            ComputeProfileResourceRequirements.Create(
                ComputeProfileResourceQuantity.Create(
                    requestsCpuMillis,
                    requestsMemoryMi),
                current.Resources.Limits),
            current.Architecture,
            current.ExtendedResources,
            current.AllowedResourceTypes.Select(ResourceTypeId.Create),
            current.TenantAccess,
            current.TargetMappings,
            current.IsEnabled,
            current.IsDefault,
            current.UpdatedAtUtc.AddMinutes(1));
        await profiles.ReplaceOneAsync(
            profile => profile.Id == current.Id,
            replacement);
        return replacement;
    }
}
