using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Testing;
using Pole.Database;
using Pole.Features.Postgres.Application;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application.Endpoints;
using Pole.Features.Postgres.Events;
using Pole.Features.ResourceConnections.Application;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Postgres.Tests.Integration.Infrastructure;
using Xunit;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.Outbox.Application;
using Pole.Features.Outbox.Application.Persistence.Delivery;
using Pole.Features.ComputeProfiles.Domain;
using System.Runtime.Loader;

namespace Pole.Features.Postgres.Tests.Integration;

[Collection(IntegrationTestCollection.Name)]
public sealed class PostgresFeatureHostTests(MongoReplicaSetFixture fixture)
{
    [Fact]
    public async Task StaticFeatureHost_EnforcesTenantAndUsesManagedSecretsWithoutAFeatureMigration()
    {
        await fixture.ResetDatabaseAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Postgres:SupportedVersions:From"] = "16",
            ["Postgres:SupportedVersions:To"] = "18"
        }).Build();
        await using var host = await FeatureTestHost.StartAsync(
            configuration,
            typeof(PostgresFeatureRegistration).Assembly,
            typeof(PostgresClusterCreatedEvent).Assembly,
            (services, settings) => services.AddPostgresFeature(settings).AddResourceConnectionsFeature(),
            endpoints => endpoints.MapPostgresClusterEndpoints(),
            null,
            services =>
        {
            services.AddSingleton(fixture.Client);
            services.AddSingleton(fixture.Database);
            services.AddScoped<EnsureRuntimeCellIngressBaseline>(_ =>
                (tenantId, runtimeCellId, targetId, namespaceName, ct) =>
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
        const string route = "/api/v1/postgres/";
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
        using var versions = await client.GetAsync(route + "supported-versions");
        Assert.Equal(HttpStatusCode.OK, versions.StatusCode);
        using var versionsJson = System.Text.Json.JsonDocument.Parse(await versions.Content.ReadAsStringAsync());
        Assert.Equal(16, versionsJson.RootElement.GetProperty("from").GetInt32());
        Assert.Equal(18, versionsJson.RootElement.GetProperty("to").GetInt32());
        using var invalid = await client.PostAsJsonAsync(route,
            PostgresTestData.CreatePostgresClusterRequest("not-an-object-id", "invalid").Body);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var invalidBody = await invalid.Content.ReadAsStringAsync();
        Assert.True(invalidBody.StartsWith("{"), invalidBody);
        using var problem = System.Text.Json.JsonDocument.Parse(invalidBody);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("Body.GroupId", out _));
        var store = new PostgresTestStore(fixture.Client, fixture.Database);
        await store.StorageProfiles.InsertOneAsync(PostgresTestData.SeedStorageProfile());
        await fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles").InsertOneAsync(PostgresTestData.SeedComputeProfile());
        var group = await store.InsertResourceGroupAsync(PostgresTestData.SeedResourceGroup());
        using var created = await client.PostAsJsonAsync(route, PostgresTestData.CreatePostgresClusterRequest(group.Id).Body);
        Assert.True(created.StatusCode == HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync());
        const string eventName = "pole.postgres/v1/postgres-cluster-created";
        var outbox = await fixture.Database.GetCollection<BsonDocument>("OutboxMessages")
            .Find(new BsonDocument("EventType", eventName)).ToListAsync();
        Assert.Single(outbox);
        var credentials = await fixture.Database.GetCollection<BsonDocument>("SecretVersions")
            .Find(new BsonDocument()).ToListAsync();
        Assert.Single(credentials);
        Assert.DoesNotContain("postgres-secret", credentials[0].ToJson());
        Assert.DoesNotContain("postgres-secret", outbox[0].ToJson());
        var eventType = host.Services.GetRequiredService<OutboxEventAssemblyCatalog>().GetRequiredEventType(eventName);
        Assert.Equal(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(eventType.Assembly));
        await host.Services.GetRequiredService<EnsurePendingOutboxEventsRegistered>().InvokeAsync();
        var history = await fixture.Database.GetCollection<BsonDocument>("__migrations")
            .Find(new BsonDocument("_id", new BsonRegularExpression("^pole.postgres/"))).ToListAsync();
        Assert.Empty(history);
        Assert.Equal(0, await fixture.Database.GetCollection<BsonDocument>("PostgresCredentials").CountDocumentsAsync(new BsonDocument()));
        host.Services.GetRequiredService<TestDeploymentTargetLookup>().RejectNewWorkloads = true;
        using var rejected = await client.PostAsJsonAsync(route,
            PostgresTestData.CreatePostgresClusterRequest(group.Id, "rejected-postgres").Body);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal(1, await store.Postgres.CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await fixture.Database.GetCollection<BsonDocument>("SecretVersions")
            .CountDocumentsAsync(new BsonDocument()));
    }
}
