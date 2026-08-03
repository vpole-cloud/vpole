using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Endpoints;
using Pole.Features.RedisServices.Application.Persistence;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.RedisServices;

public sealed class RedisIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private const string OtherTenantId = "507f1f77bcf86cd799439010";

    [Fact]
    public async Task Create_Succeeds_WithValidRequest()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());

        var result = await Mediator.Send(TestData.CreateRedisServiceRequest(resourceGroup.Id));

        result.TryGetValue(out var response, out var error);
        Assert.Null(error);
        Assert.NotNull(response);

        var savedRedis = await DbContext.Database.RedisServices().Find(redis => redis.Id == response.RedisId).FirstOrDefaultAsync();
        Assert.NotNull(savedRedis);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("redis-service-created"));
        var outbox = await DbContext.OutboxMessages
            .Find(message => message.EventType == "redis-service-created")
            .SingleAsync();
        Assert.Equal(TestData.TenantId, outbox.TenantId);
    }

    [Fact]
    public async Task Create_ReturnsValidationFailure_ForInvalidName()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var request = TestData.CreateRedisServiceRequest(resourceGroup.Id, "Bad Name");

        var result = await Mediator.Send(request);

        Assert.False(result.TryGetValue(out _, out var error));
        var validation = Assert.IsType<ValidationFailure>(error);
        Assert.Contains("Body.Name", validation.ValidationErrors.Keys);
    }

    [Fact]
    public async Task PersistCreate_AbortsRedisAndOutbox_WhenTransactionFails()
    {
        var redis = SeedRedis(TestData.TenantId, "aborted-create");
        var persistCreatedRedis = Services.GetRequiredService<PersistCreatedRedis>();

        var result = await DbContext.RunInTransactionAsync(async (
            session,
            cancellationToken) =>
        {
            await persistCreatedRedis(session, redis, cancellationToken);
            return (Result)Failures.Conflict("Abort the containing workflow.");
        });

        Assert.IsType<ConflictFailure>(result);
        Assert.Equal(0L, await DbContext.Database.RedisServices().CountDocumentsAsync(
            candidate => candidate.Id == redis.Id));
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync(
            "redis-service-created"));
    }

    [Fact]
    public async Task Update_ChangesRedisSpec()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var createResult = await Mediator.Send(TestData.CreateRedisServiceRequest(resourceGroup.Id));
        Assert.True(createResult.TryGetValue(out var created, out _));

        var updateRequest = new UpdateRedisServiceRequest(
            Id: created.RedisId,
            Body: new UpdateRedisServiceRequestBody(
                Spec: new(
                    Storage: new(10, TestData.DefaultStorageProfileId),
                    Resources: new(
                        Requests: new(2, 512),
                        Limits: new(4, 1024))),
                Tags: ["cache", "prod"]),
            TenantId: TestData.TenantId);

        var updateResult = await Mediator.Send(updateRequest);

        Assert.True(updateResult.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.NotNull(response);

        var updated = await DbContext.Database.RedisServices().Find(redis => redis.Id == created.RedisId).FirstOrDefaultAsync();
        Assert.NotNull(updated);
        Assert.Equal(10, updated.Spec.Storage.SizeGb);
        Assert.Equal(TestData.DefaultStorageProfileId, updated.Spec.Storage.StorageProfileId);
        Assert.Equal(["cache", "prod"], updated.Tags.Order());
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("redis-service-updated"));
        var outbox = await DbContext.OutboxMessages
            .Find(message => message.EventType == "redis-service-updated")
            .SingleAsync();
        Assert.Equal(TestData.TenantId, outbox.TenantId);
    }

    [Fact]
    public async Task Update_ReturnsValidationFailure_WhenStorageProfileChanges()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var createResult = await Mediator.Send(TestData.CreateRedisServiceRequest(resourceGroup.Id));
        Assert.True(createResult.TryGetValue(out var created, out _));
        var otherProfileId = ObjectId.GenerateNewId().ToString();
        await DbContext.StorageProfiles.InsertOneAsync(TestData.SeedStorageProfile(
            id: otherProfileId,
            storageClassName: "fast",
            isDefault: false));

        var updateRequest = new UpdateRedisServiceRequest(
            Id: created.RedisId,
            Body: new UpdateRedisServiceRequestBody(
                Spec: new(
                    Storage: new(5, otherProfileId),
                    Resources: new(
                        Requests: new(1, 128),
                        Limits: new(2, 256))),
                Tags: []),
            TenantId: TestData.TenantId);

        var updateResult = await Mediator.Send(updateRequest);

        Assert.False(updateResult.TryGetValue(out _, out var error));
        var validation = Assert.IsType<ValidationFailure>(error);
        Assert.Contains("Body.Spec.Storage.StorageProfileId", validation.ValidationErrors.Keys);
    }

    [Fact]
    public async Task GetList_AppliesPagination()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());

        await Mediator.Send(TestData.CreateRedisServiceRequest(resourceGroup.Id, "redis-alpha"));
        await Mediator.Send(TestData.CreateRedisServiceRequest(resourceGroup.Id, "redis-beta"));

        var result = await Mediator.Send(new GetRedisServicesRequest
        {
            TenantId = TestData.TenantId,
            PageNumber = 2,
            PageSize = 1
        });

        result.TryGetValue(out var response, out var error);
        Assert.Null(error);
        Assert.NotNull(response);
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
            TestData.TenantId));

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.IsType<NotFoundFailure>(error);
    }

    [Fact]
    public async Task GetList_ReturnsOnlySelectedTenantsRedis()
    {
        var ownRedis = await DbContext.InsertRedisAsync(
            SeedRedis(TestData.TenantId, "own-list"));
        await DbContext.InsertRedisAsync(
            SeedRedis(OtherTenantId, "foreign-list"));

        var result = await Mediator.Send(new GetRedisServicesRequest
        {
            TenantId = TestData.TenantId,
            PageNumber = 1,
            PageSize = 10
        });

        Assert.True(result.TryGetValue(out var response, out var error));
        Assert.Null(error);
        var item = Assert.Single(response.Items);
        Assert.Equal(ownRedis.Id, item.Id);
        Assert.Equal(1L, response.TotalCount);
    }

    [Fact]
    public async Task Update_ReturnsNotFound_ForAnotherTenantsRedis()
    {
        var foreignRedis = await DbContext.InsertRedisAsync(
            SeedRedis(OtherTenantId, "foreign-update"));
        var request = UpdateRequest(foreignRedis.Id, TestData.TenantId);

        var result = await Mediator.Send(request);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.IsType<NotFoundFailure>(error);
        var stored = await DbContext.Database.RedisServices()
            .Find(redis => redis.Id == foreignRedis.Id)
            .SingleAsync();
        Assert.Equal(foreignRedis.Revision, stored.Revision);
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync(
            "redis-service-updated"));
    }

    [Fact]
    public async Task PersistUpdate_ReturnsConflict_ForStaleRevision()
    {
        var current = await DbContext.InsertRedisAsync(
            SeedRedis(TestData.TenantId, "stale-update"));
        var concurrent = current.UpdateDesiredState(
            current.Spec,
            ImmutableHashSet.Create("concurrent"));
        await DbContext.Database.RedisServices().ReplaceOneAsync(
            redis => redis.Id == current.Id,
            concurrent);
        var intended = current.UpdateDesiredState(
            current.Spec,
            ImmutableHashSet.Create("intended"));
        var persistUpdatedRedis = Services.GetRequiredService<PersistUpdatedRedis>();

        var result = await DbContext.RunInTransactionAsync(
            (session, cancellationToken) => persistUpdatedRedis(
                session,
                current,
                intended,
                cancellationToken));

        Assert.IsType<ConflictFailure>(result);
        var stored = await DbContext.Database.RedisServices()
            .Find(redis => redis.Id == current.Id)
            .SingleAsync();
        Assert.Equal(ImmutableHashSet.Create("concurrent"), stored.Tags);
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync(
            "redis-service-updated"));
    }

    [Fact]
    public async Task BeginDeletion_ReturnsConflict_ForStaleRevision()
    {
        var current = await DbContext.InsertRedisAsync(
            SeedRedis(TestData.TenantId, "stale-delete"));
        var concurrent = current.UpdateDesiredState(
            current.Spec,
            ImmutableHashSet.Create("concurrent"));
        await DbContext.Database.RedisServices().ReplaceOneAsync(
            redis => redis.Id == current.Id,
            concurrent);
        var deleting = current.BeginDeletion(DateTime.UtcNow);
        var beginRedisDeletion = Services.GetRequiredService<BeginRedisDeletion>();

        var result = await DbContext.RunInTransactionAsync(
            (session, cancellationToken) => beginRedisDeletion(
                session,
                current,
                deleting,
                cancellationToken));

        Assert.IsType<ConflictFailure>(result);
        var stored = await DbContext.Database.RedisServices()
            .Find(redis => redis.Id == current.Id)
            .SingleAsync();
        Assert.IsType<ActiveRedisLifecycle>(stored.Lifecycle);
        Assert.Equal(ImmutableHashSet.Create("concurrent"), stored.Tags);
    }

    [Fact]
    public async Task Delete_MarksRedisDeleting_AndRequestsDeploymentDeletion()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var createResult = await Mediator.Send(TestData.CreateRedisServiceRequest(resourceGroup.Id));
        Assert.True(createResult.TryGetValue(out var created, out _));

        var deleteResult = await Mediator.Send(new DeleteRedisServiceRequest(created.RedisId, TestData.TenantId));

        Assert.True(deleteResult.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.Equal(created.RedisId, response.Id);

        var deleting = await DbContext.Database.RedisServices()
            .Find(redis => redis.Id == created.RedisId)
            .SingleAsync();
        Assert.IsType<Pole.Features.RedisServices.Domain.DeletingRedisLifecycle>(
            deleting.Lifecycle);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(
            "resource-deployment-deletion-requested"));
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync(
            "redis-service-deleted"));
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_WhenRedisDoesNotExist()
    {
        var result = await Mediator.Send(new DeleteRedisServiceRequest("507f1f77bcf86cd799439012", TestData.TenantId));

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.IsType<NotFoundFailure>(error);
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_ForAnotherTenantsRedis()
    {
        var foreignRedis = await DbContext.InsertRedisAsync(
            SeedRedis(OtherTenantId, "foreign-delete"));

        var result = await Mediator.Send(new DeleteRedisServiceRequest(
            foreignRedis.Id,
            TestData.TenantId));

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.IsType<NotFoundFailure>(error);
        var stored = await DbContext.Database.RedisServices()
            .Find(redis => redis.Id == foreignRedis.Id)
            .SingleAsync();
        Assert.IsType<ActiveRedisLifecycle>(stored.Lifecycle);
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync(
            "resource-deployment-deletion-requested"));
    }

    [Fact]
    public async Task FinalizeDeletion_IsTenantScoped_AtomicAndIdempotent()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(
            TestData.SeedResourceGroup());
        var createResult = await Mediator.Send(
            TestData.CreateRedisServiceRequest(resourceGroup.Id, "finalize-redis"));
        Assert.True(createResult.TryGetValue(out var created, out _));
        var deleteResult = await Mediator.Send(new DeleteRedisServiceRequest(
            created.RedisId,
            TestData.TenantId));
        Assert.True(deleteResult.TryGetValue(out _, out _));

        var deployment = await DbContext.ResourceDeployments
            .Find(candidate =>
                candidate.TenantId == TestData.TenantId &&
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

        Assert.IsAssignableFrom<ISuccess>(wrongTenantResult);
        Assert.Equal(1L, await DbContext.Database.RedisServices().CountDocumentsAsync(
            redis => redis.Id == created.RedisId));
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync(
            "redis-service-deleted"));

        var result = await finalizeRedisDeletion(
            TestData.TenantId,
            created.RedisId,
            CancellationToken.None);
        var repeatedResult = await finalizeRedisDeletion(
            TestData.TenantId,
            created.RedisId,
            CancellationToken.None);

        Assert.IsAssignableFrom<ISuccess>(result);
        Assert.IsAssignableFrom<ISuccess>(repeatedResult);
        Assert.Equal(0L, await DbContext.Database.RedisServices().CountDocumentsAsync(
            redis => redis.Id == created.RedisId));
        Assert.Equal(0L, await DbContext.ResourceDeployments.CountDocumentsAsync(
            candidate =>
                candidate.TenantId == TestData.TenantId &&
                candidate.ResourceType == RedisResourceDeployment.ResourceType.Value &&
                candidate.ResourceId == created.RedisId));
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync(
            "redis-service-deleted"));
        var outbox = await DbContext.OutboxMessages
            .Find(message => message.EventType == "redis-service-deleted")
            .SingleAsync();
        Assert.Equal(TestData.TenantId, outbox.TenantId);
    }

    private static Redis SeedRedis(
        string tenantId,
        string name) =>
        RedisFactory.Create(
            tenantId,
            name,
            ObjectId.GenerateNewId().ToString(),
            TestData.ValidRedisSpec().ToDomain(),
            RedisFactory.DefaultCreateOptions);

    private static UpdateRedisServiceRequest UpdateRequest(
        string redisId,
        string tenantId) =>
        new(
            redisId,
            new UpdateRedisServiceRequestBody(
                TestData.ValidRedisSpec(),
                ImmutableHashSet.Create("updated")),
            tenantId);
}
