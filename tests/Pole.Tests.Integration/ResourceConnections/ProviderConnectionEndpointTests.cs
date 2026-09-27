using System.Collections.Immutable;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FSharp.Core;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Postgres.Application;
using Pole.Features.Postgres.Application.Kubernetes;
using Pole.Features.Postgres.Contracts;
using Pole.Features.Postgres.Domain;
using Pole.Features.RedisServices.Application;
using Pole.Features.RedisServices.Contracts;
using Pole.Features.RedisServices.Domain;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Contracts;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceConnections;

[Collection(IntegrationTestCollection.Name)]
public sealed class ProviderConnectionEndpointTests(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    private ServiceProvider services = null!;
    private IServiceScope scope = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();
        var collection = new ServiceCollection();
        collection.AddSingleton(fixture.Database);
        collection.AddRedisServicesFeature();
        collection.AddWebServicesFeature();
        collection.AddPostgresFeature(new ConfigurationBuilder().Build());
        services = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        scope = services.CreateScope();
    }

    public async Task DisposeAsync()
    {
        scope.Dispose();
        await services.DisposeAsync();
    }

    [Fact]
    public async Task WebCatalog_ReadsDesiredPortsInCallerTransaction_AndExcludesUnsafeEndpoints()
    {
        var web = TestData.SeedWebService(Id()) with
        {
            Spec = TestData.SeedWebService(Id()).Spec with
            {
                Config = new WebServiceConfig(ImmutableDictionary<string, string>.Empty,
                [
                    new PortMapping(80, "8080", "TCP", " HTTP "),
                    new PortMapping(9000, "9001", "UDP", "datagram"),
                    new PortMapping(9002, "named-target", "TCP", "unresolved"),
                    new PortMapping(9003, "9003", "TCP", "duplicate"),
                    new PortMapping(9004, "9004", "TCP", "DUPLICATE"),
                    new PortMapping(9005, "9005", "TCP", "")
                ])
            }
        };
        using var session = await fixture.Client.StartSessionAsync();
        session.StartTransaction();
        await fixture.Database.GetCollection<WebService>("WebServices").InsertOneAsync(session, web);
        var lookup = scope.ServiceProvider.GetRequiredService<LoadWebServiceConnectionEndpoints>();

        var result = await lookup(web.TenantId, web.Id, session);

        var found = Assert.IsType<WebServiceConnectionEndpointsFound>(result.Value);
        Assert.Equal(web.Id, found.ResourceId);
        Assert.Equal($"{web.Name}-svc", found.ServiceName);
        var endpoint = Assert.Single(found.Endpoints);
        Assert.Equal("http", endpoint.Id);
        Assert.Equal(80, endpoint.ServicePort);
        Assert.Equal(8080, endpoint.TargetPort);
        Assert.IsType<WebServiceConnectionResourceMissing>((await lookup(Id(), web.Id, session)).Value);
        await session.AbortTransactionAsync();
        Assert.Equal(0, await fixture.Database.GetCollection<WebService>("WebServices")
            .CountDocumentsAsync(item => item.Id == web.Id));
    }

    [Fact]
    public async Task RedisCatalog_IsTenantScoped_AndReportsDeletionWithoutPublishingCredentials()
    {
        var redis = RedisFactory.Create(TestData.TenantId, "cache", Id(),
            new RedisSpec(new Storage(1, Id())), RedisFactory.DefaultCreateOptions);
        var collection = fixture.Database.GetCollection<Redis>("RedisServices");
        await collection.InsertOneAsync(redis);
        var lookup = scope.ServiceProvider.GetRequiredService<LoadRedisConnectionEndpoints>();
        using var session = await fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var found = Assert.IsType<RedisConnectionEndpointsFound>((await lookup(redis.TenantId, redis.Id, session)).Value);
        Assert.Equal("cache-svc", found.ServiceName);
        var endpoint = Assert.Single(found.Endpoints);
        Assert.Equal("redis", endpoint.Id);
        Assert.Equal(6379, endpoint.ServicePort);
        Assert.Equal(6379, endpoint.TargetPort);
        Assert.IsType<RedisConnectionResourceMissing>((await lookup(Id(), redis.Id, session)).Value);
        await collection.ReplaceOneAsync(session, item => item.Id == redis.Id, redis.BeginDeletion(DateTime.UtcNow));
        Assert.IsType<RedisConnectionResourceDeleting>((await lookup(redis.TenantId, redis.Id, session)).Value);
        await session.AbortTransactionAsync();
    }

    [Theory]
    [InlineData(PostgresDeploymentMode.Operator)]
    [InlineData(PostgresDeploymentMode.StatefulSet)]
    public async Task PostgresCatalog_PublishesOnlyReadWriteEndpoint_ForBothProviders(PostgresDeploymentMode mode)
    {
        var postgres = PostgresCluster.Create(TestData.TenantId, "database", Id(), 17, mode, Id(),
            new CreatePostgresClusterOptions(FSharpOption<string>.Some(Id()), 1, 5));
        var collection = fixture.Database.GetCollection<PostgresCluster>("Postgres");
        await collection.InsertOneAsync(postgres);
        var lookup = scope.ServiceProvider.GetRequiredService<LoadPostgresConnectionEndpoints>();
        using var session = await fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var found = Assert.IsType<PostgresConnectionEndpointsFound>((await lookup(postgres.TenantId, postgres.Id, session)).Value);
        Assert.Equal(PostgresClusterKubernetesBuilder.buildReadWriteServiceName(postgres.Id), found.ServiceName);
        var endpoint = Assert.Single(found.Endpoints);
        Assert.Equal("postgres", endpoint.Id);
        Assert.Equal(5432, endpoint.ServicePort);
        Assert.Equal(5432, endpoint.TargetPort);
        Assert.IsType<PostgresConnectionResourceMissing>((await lookup(Id(), postgres.Id, session)).Value);
        await collection.ReplaceOneAsync(session, item => item.Id == postgres.Id, postgres.BeginDeletion(DateTime.UtcNow));
        Assert.IsType<PostgresConnectionResourceDeleting>((await lookup(postgres.TenantId, postgres.Id, session)).Value);
        await session.AbortTransactionAsync();
    }

    private static string Id() => ObjectId.GenerateNewId().ToString();
}
