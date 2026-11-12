using System.Collections.Immutable;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FSharp.Core;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Postgres.Application;
using Pole.Features.Postgres.Application.Kubernetes;
using Pole.Features.Postgres.Domain;
using Pole.Features.RedisServices.Application;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceConnections;

[Collection(IntegrationTestCollection.Global4)]
public sealed class ProviderConnectionEndpointTests(
    MongoReplicaSetFixture fixture
) : MongoIntegrationTestBase(fixture)
{
    private IServiceScope scope = null!;
    private ServiceProvider services = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        var collection = new ServiceCollection();
        collection.AddSingleton(Fixture.Database);
        collection.AddRedisServicesFeature();
        collection.AddWebServicesFeature();
        collection.AddPostgresFeature(new ConfigurationBuilder().Build());
        services = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        scope = services.CreateScope();
    }

    public override async Task DisposeAsync()
    {
        scope.Dispose();
        await services.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task WebCatalog_ReadsDesiredPortsInCallerTransaction_AndExcludesUnsafeEndpoints()
    {
        var web = Data.SeedWebService(Id()) with
        {
            Spec = Data.SeedWebService(Id()).Spec with
            {
                Config = new WebServiceConfig
                {
                    EnvironmentVariables = ImmutableDictionary<string, string>.Empty,
                    PortMappings =
                    [
                        new PortMapping(80, "8080", "TCP", " HTTP "), new PortMapping(9000, "9001", "UDP", "datagram"),
                        new PortMapping(9002, "named-target", "TCP", "unresolved"),
                        new PortMapping(9003, "9003", "TCP", "duplicate"),
                        new PortMapping(9004, "9004", "TCP", "DUPLICATE"),
                        new PortMapping(9005, "9005", "TCP", ""),
                    ],
                    ConnectionEnvironmentReferences = ImmutableDictionary<string, ConnectionEnvironmentReference>.Empty,
                    SecretEnvironmentReferences = ImmutableDictionary<string, SecretEnvironmentReference>.Empty,
                },
            },
        };
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        await Fixture.Database.GetCollection<WebService>("WebServices").InsertOneAsync(session, web);
        var lookup = Destination("pole/web-service");

        var result = await lookup.LoadAsync(
            new LoadResourceConnectionDestinationRequest(TenantId: web.TenantId, ResourceId: web.Id),
            session: session);

        var found = Assert.IsType<ResourceConnectionEndpointsFound>(result.Value);
        var endpoint = Assert.Single(found.Endpoints);
        Assert.Equal("http", endpoint.Id);
        Assert.Equal($"{web.Name}-svc", endpoint.ServiceName);
        Assert.Equal(80, endpoint.ServicePort.Value);
        Assert.Equal(8080, endpoint.TargetPort.Value);
        Assert.Equal("HTTP", endpoint.DisplayName);
        Assert.Equal("tcp", endpoint.Protocol);
        Assert.Empty(endpoint.Configuration);
        Assert.IsType<ResourceConnectionDestinationMissing>(
            (await lookup.LoadAsync(
                new LoadResourceConnectionDestinationRequest(TenantId: Id(), ResourceId: web.Id),
                session: session)).Value);
        await session.AbortTransactionAsync();
        Assert.Equal(
            0,
            await Fixture.Database
                .GetCollection<WebService>("WebServices")
                .CountDocumentsAsync(item => item.Id == web.Id));
    }

    [Fact]
    public async Task RedisCatalog_IsTenantScoped_AndReportsDeletionWithoutPublishingCredentials()
    {
        var redis = RedisFactory.Create(
            Data.TenantId,
            "cache",
            Id(),
            new RedisSpec(new Storage(1, Id())),
            RedisFactory.DefaultCreateOptions);
        var collection = Fixture.Database.GetCollection<Redis>("RedisServices");
        await collection.InsertOneAsync(redis);
        var lookup = Destination("pole/redis");
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var found = Assert.IsType<ResourceConnectionEndpointsFound>(
            (await lookup.LoadAsync(
                new LoadResourceConnectionDestinationRequest(TenantId: redis.TenantId, ResourceId: redis.Id),
                session: session)).Value);
        var endpoint = Assert.Single(found.Endpoints);
        Assert.Equal("redis", endpoint.Id);
        Assert.Equal("cache-svc", endpoint.ServiceName);
        Assert.Equal(6379, endpoint.ServicePort.Value);
        Assert.Equal(6379, endpoint.TargetPort.Value);
        Assert.Equal("redis", endpoint.Protocol);
        Assert.Equal("0", Assert.Single(endpoint.Configuration).Value);
        Assert.IsType<ResourceConnectionDestinationMissing>(
            (await lookup.LoadAsync(
                new LoadResourceConnectionDestinationRequest(TenantId: Id(), ResourceId: redis.Id),
                session: session)).Value);
        await collection.ReplaceOneAsync(session, item => item.Id == redis.Id, redis.BeginDeletion(DateTime.UtcNow));
        Assert.IsType<ResourceConnectionDestinationDeleting>(
            (await lookup.LoadAsync(
                new LoadResourceConnectionDestinationRequest(TenantId: redis.TenantId, ResourceId: redis.Id),
                session: session)).Value);
        await session.AbortTransactionAsync();
    }

    [Fact]
    public async Task PostgresCatalog_PublishesOnlyReadWriteEndpoint()
    {
        var postgres = PostgresCluster.Create(
            Data.TenantId,
            "database",
            Id(),
            17,
            Id(),
            new CreatePostgresClusterOptions(FSharpOption<string>.Some(Id()), 1, 5));
        var collection = Fixture.Database.GetCollection<PostgresCluster>("Postgres");
        await collection.InsertOneAsync(postgres);
        var lookup = Destination("pole/postgresql");
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var found = Assert.IsType<ResourceConnectionEndpointsFound>(
            (await lookup.LoadAsync(
                new LoadResourceConnectionDestinationRequest(TenantId: postgres.TenantId, ResourceId: postgres.Id),
                session: session)).Value);
        var endpoint = Assert.Single(found.Endpoints);
        Assert.Equal("postgres", endpoint.Id);
        Assert.Equal(PostgresClusterKubernetesBuilder.buildReadWriteServiceName(postgres.Id), endpoint.ServiceName);
        Assert.Equal(5432, endpoint.ServicePort.Value);
        Assert.Equal(5432, endpoint.TargetPort.Value);
        Assert.Equal("postgresql", endpoint.Protocol);
        Assert.Equal(postgres.Id, Assert.Single(endpoint.Configuration).Value);
        Assert.IsType<ResourceConnectionDestinationMissing>(
            (await lookup.LoadAsync(
                new LoadResourceConnectionDestinationRequest(TenantId: Id(), ResourceId: postgres.Id),
                session: session)).Value);
        await collection.ReplaceOneAsync(
            session,
            item => item.Id == postgres.Id,
            postgres.BeginDeletion(DateTime.UtcNow));
        Assert.IsType<ResourceConnectionDestinationDeleting>(
            (await lookup.LoadAsync(
                new LoadResourceConnectionDestinationRequest(TenantId: postgres.TenantId, ResourceId: postgres.Id),
                session: session)).Value);
        await session.AbortTransactionAsync();
    }

    [Fact]
    public async Task AdvisoryCatalog_BatchesOnlyAvailableResourcesFromRequestedTenant()
    {
        var available = RedisFactory.Create(
            Data.TenantId,
            "available",
            Id(),
            new RedisSpec(new Storage(1, Id())),
            RedisFactory.DefaultCreateOptions);
        var deleting = RedisFactory.Create(
                Data.TenantId,
                "deleting",
                Id(),
                new RedisSpec(new Storage(1, Id())),
                RedisFactory.DefaultCreateOptions)
            .BeginDeletion(DateTime.UtcNow);
        var otherTenant = RedisFactory.Create(
            Id(),
            "other",
            Id(),
            new RedisSpec(new Storage(1, Id())),
            RedisFactory.DefaultCreateOptions);
        await Fixture.Database
            .GetCollection<Redis>("RedisServices")
            .InsertManyAsync([available, deleting, otherTenant]);
        var lookup = Destination("pole/redis");

        var found = await lookup.LoadAvailableAsync(Data.TenantId, [available.Id, deleting.Id, otherTenant.Id, Id()]);

        var endpoint = Assert.Single(Assert.Single(found).Value.Endpoints);
        Assert.Equal(available.Id, Assert.Single(found).Key);
        Assert.Equal("available-svc", endpoint.ServiceName);
        Assert.Empty(await lookup.LoadAvailableAsync(Data.TenantId, []));
    }

    private IResourceConnectionDestinationCapability Destination(string resourceType) => scope.ServiceProvider
        .GetServices<IResourceConnectionDestinationCapability>()
        .Single(capability => capability.ResourceType.Value == resourceType);

    private static string Id() => ObjectId.GenerateNewId().ToString();
}
