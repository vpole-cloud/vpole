using MongoDB.Driver;
using Pole.Features.RedisServices.Endpoints;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.RedisServices;

public sealed class RedisIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Create_Succeeds_WithValidRequest()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());

        var result = await Mediator.Send(TestData.CreateRedisServiceRequest(resourceGroup.Id));

        result.TryGetValue(out var response, out var error);
        Assert.Null(error);
        Assert.NotNull(response);

        var savedRedis = await DbContext.RedisServices.Find(redis => redis.Id == response.RedisId).FirstOrDefaultAsync();
        Assert.NotNull(savedRedis);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("create-redis-service-requested"));
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
    public async Task Update_ChangesRedisSpec()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var createResult = await Mediator.Send(TestData.CreateRedisServiceRequest(resourceGroup.Id));
        Assert.True(createResult.TryGetValue(out var created, out _));

        var updateRequest = new UpdateRedisServiceRequest(
            Id: created.RedisId,
            Body: new UpdateRedisServiceRequestBody(
                ResourceGroupId: resourceGroup.Id,
                Spec: new(
                    Storage: new(10, "fast"),
                    Resources: new(
                        Requests: new(2, 512),
                        Limits: new(4, 1024))),
                Tags: ["cache", "prod"]));

        var updateResult = await Mediator.Send(updateRequest);

        Assert.True(updateResult.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.NotNull(response);

        var updated = await DbContext.RedisServices.Find(redis => redis.Id == created.RedisId).FirstOrDefaultAsync();
        Assert.NotNull(updated);
        Assert.Equal(10, updated.Spec.Storage.SizeGb);
        Assert.Equal("fast", updated.Spec.Storage.StorageClassName);
        Assert.Equal(["cache", "prod"], updated.Tags.Order());
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("update-redis-service"));
    }

    [Fact]
    public async Task GetList_AppliesPagination()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());

        await Mediator.Send(TestData.CreateRedisServiceRequest(resourceGroup.Id, "redis-alpha"));
        await Mediator.Send(TestData.CreateRedisServiceRequest(resourceGroup.Id, "redis-beta"));

        var result = await Mediator.Send(new GetRedisServicesRequest
        {
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
    public async Task Delete_RemovesRedisAndStoresOutboxEvent()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var createResult = await Mediator.Send(TestData.CreateRedisServiceRequest(resourceGroup.Id));
        Assert.True(createResult.TryGetValue(out var created, out _));

        var deleteResult = await Mediator.Send(new DeleteRedisServiceRequest(created.RedisId));

        Assert.True(deleteResult.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.Equal(created.RedisId, response.Id);

        var deleted = await DbContext.RedisServices.Find(redis => redis.Id == created.RedisId).FirstOrDefaultAsync();
        Assert.Null(deleted);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("delete-redis-service"));
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_WhenRedisDoesNotExist()
    {
        var result = await Mediator.Send(new DeleteRedisServiceRequest("507f1f77bcf86cd799439012"));

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.IsType<NotFoundFailure>(error);
    }
}
