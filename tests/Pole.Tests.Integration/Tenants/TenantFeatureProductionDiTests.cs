using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Tenants.Application;
using Pole.Features.Tenants.Application.Members;
using Pole.Features.Tenants.Application.Tenants;
using Pole.Features.Tenants.Contracts;
using Pole.Features.Tenants.Domain;
using Pole.Features.Users.Application;
using Microsoft.Extensions.Configuration;
using Pole.Mediator;
using Pole.Platform.Hosting;
using Pole.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Sdk;

namespace Pole.Tests.Integration.Tenants;

[Collection(IntegrationTestCollection.Global3)]
public sealed class TenantFeatureProductionDiTests(
    MongoReplicaSetFixture fixture
) : MongoIntegrationTestBase(fixture)
{
    [Fact]
    public void FeatureRegistration_ResolvesTheTenantCapabilityGraph()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMongoDatabase>(Fixture.Database);
        services.AddSingleton(Fixture.Client);
        services.AddSingleton<MongoRunTransaction>();
        services.AddSingleton<StoreOutboxWrites>(Fixture.StoreOutboxWrites);
        services.AddSingleton(TimeProvider.System);
        services.AddUsersFeature(new ConfigurationBuilder().Build());
        services.AddTenantsFeature();

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType.Name.Contains("DbContext", StringComparison.Ordinal));
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(LoadTenantIdentity) &&
                descriptor.Lifetime == ServiceLifetime.Scoped);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        AssertResolved<LoadTenant>(scope.ServiceProvider);
        AssertResolved<LoadTenantsForUser>(scope.ServiceProvider);
        AssertResolved<PersistCreatedTenant>(scope.ServiceProvider);
        AssertResolved<PersistUpdatedTenantMemberRole>(scope.ServiceProvider);
        AssertResolved<PersistRemovedTenantMember>(scope.ServiceProvider);
        AssertResolved<EnsureDefaultTenant>(scope.ServiceProvider);
        AssertResolved<LoadTenantIdentity>(scope.ServiceProvider);
        AssertResolved<EnsureTenantsExist>(scope.ServiceProvider);
    }

    [Fact]
    public void FeatureRegistration_PreservesTheTenantAuthorizationPipelineOrder()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddUsersFeature(new ConfigurationBuilder().Build());
        services.AddTenantsFeature();
        services.AddPoleMediator();

        var pipelineTypes = services.Where(descriptor => descriptor.ServiceType == typeof(IPipelineBehavior<,>))
            .Select(descriptor => descriptor.ImplementationType)
            .ToArray();

        Assert.Equal(
            [
                typeof(LoggingBehavior<,>),
                typeof(ValidationBehavior<,>),
                typeof(TenantAuthorizationBehavior<,>),
                typeof(TenantMemberAuthorizationBehavior<,>),
            ],
            pipelineTypes);
    }

    [Fact]
    public async Task TenantExistenceContract_RejectsMalformedObjectIds()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Fixture.Database);
        services.AddSingleton(TimeProvider.System);
        services.AddUsersFeature(new ConfigurationBuilder().Build());
        services.AddTenantsFeature();
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var ensureTenantsExist = scope.ServiceProvider.GetRequiredService<EnsureTenantsExist>();

        await Assert.ThrowsAsync<ArgumentException>(() => ensureTenantsExist(["not-an-object-id"]));
    }

    [Fact]
    public async Task TenantContracts_ReturnNamedSuccessAndMissingOutcomes()
    {
        var tenant = TenantFactory.Create("Contract lookup");
        var missingTenantId = ObjectId.GenerateNewId().ToString();
        await Fixture.Database.GetCollection<Tenant>("Tenants").InsertOneAsync(tenant);
        var services = new ServiceCollection();
        services.AddSingleton(Fixture.Database);
        services.AddSingleton(TimeProvider.System);
        services.AddUsersFeature(new ConfigurationBuilder().Build());
        services.AddTenantsFeature();
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var ensure = scope.ServiceProvider.GetRequiredService<EnsureTenantsExist>();
        var load = scope.ServiceProvider.GetRequiredService<LoadTenantIdentity>();

        Assert.True(await ensure([]) is TenantsExist);
        Assert.True(await ensure([tenant.Id, tenant.Id]) is TenantsExist);
        var missing = await ensure([tenant.Id, missingTenantId, missingTenantId]);
        Assert.True(missing is TenantSetNotFound);
        var missingIds = missing switch
        {
            TenantSetNotFound missingSet => missingSet.TenantIds,
            TenantsExist => throw new XunitException("Expected missing tenants."),
        };
        Assert.Equal([missingTenantId], missingIds);
        Assert.True(await load(tenant.Id) is TenantIdentitySnapshot { DisplayName: "Contract lookup" });
        Assert.True(
            await load(missingTenantId) is TenantIdentityNotFound notFound && notFound.TenantId == missingTenantId);
    }

    [Fact]
    public void FeatureRegistration_RegistersOnlyPipelineRejectionMappings()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddUsersFeature(new ConfigurationBuilder().Build());
        services.AddTenantsFeature();
        using var provider = services.BuildServiceProvider();
        var mappings = provider.GetServices<HttpProblemMapping>().ToArray();

        Assert.NotEmpty(mappings);
        Assert.All(mappings, mapping => Assert.True(typeof(IMediatorRejection).IsAssignableFrom(mapping.MappedType)));
        Assert.Contains(mappings, mapping => mapping.MappedType == typeof(TenantNotFound));
        Assert.Contains(mappings, mapping => mapping.MappedType == typeof(TenantMemberNotFound));
    }

    private static void AssertResolved<T>(IServiceProvider provider)
        where T : notnull => Assert.NotNull(provider.GetRequiredService<T>());
}
