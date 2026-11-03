using Pole.Platform.Hosting;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Testing;
using Pole.Database;
using Pole.Features.RedisServices.Application;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Migrations;
using Pole.Features.RedisServices.Events;
using Pole.Features.ResourceConnections.Application;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.RedisServices.Tests.Integration.Infrastructure;
using Xunit;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.Outbox.Application;
using Pole.Features.Outbox.Application.Persistence.Delivery;
using Pole.Features.ComputeProfiles.Domain;
using System.Runtime.Loader;

namespace Pole.Features.RedisServices.Tests.Integration.RedisServices;

[Collection(IntegrationTestCollection.Name)]
public sealed class RedisFeatureHostTests(MongoReplicaSetFixture fixture)
{
    [Fact]
    public async Task StaticFeatureHost_EnforcesTenantAndRunsScopedMigration()
    {
        await fixture.ResetDatabaseAsync();
        var configuration = new ConfigurationBuilder().Build();
        await using var host = await FeatureTestHost.StartAsync(
            configuration,
            typeof(RedisServicesFeatureRegistration).Assembly,
            typeof(RedisServiceCreatedEvent).Assembly,
            (services, _) => services.AddRedisServicesFeature().AddResourceConnectionsFeature(),
            endpoints => endpoints.MapRedisServiceEndpoints(),
            null,
            services =>
        {
            services.AddFeatureMigrations("pole.redis", typeof(RedisSchemaValidationMigration).Assembly);
            services.AddSingleton(fixture.Client);
            services.AddSingleton(fixture.Database);
            services.AddScoped<EnsureRuntimeCellIngressBaseline>(_ =>
(request, ct) =>
                    Task.FromResult<EnsureRuntimeCellIngressBaselineOutcome>(
                        new RuntimeCellIngressBaselineReady()));
            services.AddTestDeploymentTargets();
            var storage = new TestStorageDiscoveryService("standard");
            services.AddSingleton<DiscoverKubernetesStorageClasses>(storage.GetStorageClassesAsync);
            services.AddSingleton<DiscoverKubernetesStorageClassNames>(storage.GetStorageClassNamesAsync);
            services.AddSingleton<DiscoverKubernetesPersistentVolumes>(storage.GetPersistentVolumesAsync);
            services.AddSingleton<DiscoverKubernetesPersistentVolumeClaims>(storage.GetPersistentVolumeClaimsAsync);
        });
        await host.SeedTenantAsync();
        using var client = host.GetClient();
        const string route = "/api/v1/redis-services/";
        using var anonymous = await client.GetAsync(route + "?pageNumber=1&pageSize=10");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Add(FeatureTestAuthentication.UserHeader, TestPlatformDefaults.UserId);
        using var missing = await client.GetAsync(route + "?pageNumber=1&pageSize=10");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        client.DefaultRequestHeaders.Add("X-Pole-Tenant-ID", TestPlatformDefaults.TenantId);
        using var response = await client.GetAsync(route + "?pageNumber=1&pageSize=10");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var otherUser = new HttpRequestMessage(HttpMethod.Get, route + "?pageNumber=1&pageSize=10");
        otherUser.Headers.Add(FeatureTestAuthentication.UserHeader, "other-user");
        using var forbidden = await client.SendAsync(otherUser);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        const string absentId = "507f1f77bcf86cd799439012";
        using var missingRedis = await client.GetAsync(route + absentId);
        Assert.Equal(HttpStatusCode.NotFound, missingRedis.StatusCode);
        using var missingDelete = await client.DeleteAsync(route + absentId);
        Assert.Equal(HttpStatusCode.NotFound, missingDelete.StatusCode);
        var store = new RedisTestStore(fixture.Client, fixture.Database);
        await store.StorageProfiles.InsertOneAsync(RedisTestData.SeedStorageProfile());
        await fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles").InsertOneAsync(RedisTestData.SeedComputeProfile());
        var group = await store.InsertResourceGroupAsync(RedisTestData.SeedResourceGroup());
        using var created = await client.PostAsJsonAsync(route, RedisTestData.CreateRedisServiceRequest(group.Id).Body);
        Assert.True(created.StatusCode == HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync());
        var creation = await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        using var details = await client.GetAsync(route + creation.GetProperty("redisId").GetString());
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        var metadata = await details.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("Active", metadata.GetProperty("lifecycle").GetString());
        Assert.Equal(1, metadata.GetProperty("revision").GetInt64());
        using var resize = await client.PutAsJsonAsync(route + creation.GetProperty("redisId").GetString(),
            new { spec = new { storage = new { sizeGb = 2, storageProfileId = metadata.GetProperty("spec").GetProperty("storage").GetProperty("storageProfileId").GetString() } }, tags = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resize.StatusCode);
        Assert.Contains("redis-services.storage-resize-unsupported", await resize.Content.ReadAsStringAsync());
        using var grow = await client.PutAsJsonAsync(route + creation.GetProperty("redisId").GetString(),
            new { spec = new { storage = new { sizeGb = 10, storageProfileId = metadata.GetProperty("spec").GetProperty("storage").GetProperty("storageProfileId").GetString() } }, tags = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, grow.StatusCode);
        using var unchanged = await client.GetAsync(route + creation.GetProperty("redisId").GetString());
        var unchangedMetadata = await unchanged.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(1, unchangedMetadata.GetProperty("revision").GetInt64());
        Assert.Equal(5, unchangedMetadata.GetProperty("spec").GetProperty("storage").GetProperty("sizeGb").GetInt32());
        const string eventName = "pole.redis/v1/redis-service-created";
        var outbox = await fixture.Database.GetCollection<BsonDocument>("OutboxMessages")
            .Find(new BsonDocument("EventType", eventName)).ToListAsync();
        Assert.Single(outbox);
        var eventType = host.Services.GetRequiredService<OutboxEventAssemblyCatalog>().GetRequiredEventType(eventName);
        Assert.Equal(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(eventType.Assembly));
        await host.Services.GetRequiredService<EnsurePendingOutboxEventsRegistered>().InvokeAsync();
        var history = await fixture.Database.GetCollection<BsonDocument>("__migrations")
            .Find(new BsonDocument("_id", new BsonRegularExpression("^pole.redis/"))).ToListAsync();
        Assert.Equal(
            ["pole.redis/6a700001b213917a38c38001", "pole.redis/6a700002b213917a38c38001"],
            history.Select(item => item["_id"].AsString).Order(StringComparer.Ordinal));
    }
}
