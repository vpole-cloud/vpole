using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.Core.Multitenancy;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Postgres.Application;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application.Endpoints;
using Pole.Features.Postgres.Events;
using Pole.Features.ResourceConnections.Application;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.Tenants.Domain;
using Pole.Mediator;
using Pole.Platform.Hosting;
using Pole.Testing;
using Xunit;

namespace Pole.Features.Postgres.Tests.Integration.Infrastructure;

[Collection(IntegrationTestCollection.Name)]
public abstract class MediatorIntegrationTestBase(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    private readonly TestEncryptionSettings _encryption = new();
    private ServiceProvider? _serviceProvider;
    private IServiceScope? _scope;

    protected MongoReplicaSetFixture Fixture { get; } = fixture;
    protected PostgresTestStore DbContext { get; private set; } = null!;
    protected IMediator Mediator => _scope!.ServiceProvider.GetRequiredService<IMediator>();
    protected IServiceProvider Services => _scope!.ServiceProvider;

    public virtual async Task InitializeAsync()
    {
        await Fixture.ResetDatabaseAsync();
        DbContext = new PostgresTestStore(Fixture.Client, Fixture.Database);

        var configuration = new ConfigurationBuilder()
            .AddConfiguration(_encryption.Configuration)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["kubernetes:NamespaceNamePrefix"] = "test"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Fixture.Client);
        services.AddSingleton(Fixture.Database);
        services.AddSingleton<MongoRunTransaction>();
        var storageDiscovery = new TestStorageDiscoveryService("standard", "fast", "bulk");
        services.AddSingleton(storageDiscovery);
        services.AddSingleton<DiscoverKubernetesStorageClasses>(storageDiscovery.GetStorageClassesAsync);
        services.AddSingleton<DiscoverKubernetesStorageClassNames>(storageDiscovery.GetStorageClassNamesAsync);
        services.AddSingleton<DiscoverKubernetesPersistentVolumes>(storageDiscovery.GetPersistentVolumesAsync);
        services.AddSingleton<DiscoverKubernetesPersistentVolumeClaims>(storageDiscovery.GetPersistentVolumeClaimsAsync);
        services.AddPolePlatform(
            configuration,
            [typeof(PostgresClusterEndpoints).Assembly],
            [typeof(PostgresClusterCreatedEvent).Assembly],
            []);
        services.AddPostgresFeature(configuration);
        services.AddResourceConnectionsFeature();
        services.AddTestDeploymentTargets();
        ReplaceTenantAuthorizationWithFake(services);
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider(validateScopes: true);
        await _serviceProvider.InitializePolePlatformAsync();

        await DbContext.StorageProfiles.InsertOneAsync(PostgresTestData.SeedStorageProfile());
        await Fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles")
            .InsertOneAsync(PostgresTestData.SeedComputeProfile());
        await DbContext.Tenants.InsertOneAsync(TenantFactory.Create("Test tenant", PostgresTestData.TenantId));
        _scope = _serviceProvider.CreateScope();
    }

    protected virtual void ConfigureServices(IServiceCollection services) { }

    public virtual Task DisposeAsync()
    {
        _scope?.Dispose();
        _serviceProvider?.Dispose();
        _encryption.Dispose();
        return Task.CompletedTask;
    }

    private static void ReplaceTenantAuthorizationWithFake(IServiceCollection services)
    {
        var descriptor = services.SingleOrDefault(d =>
            d.ServiceType == typeof(IPipelineBehavior<,>) &&
            d.ImplementationType == typeof(TenantAuthorizationBehavior<,>));
        if (descriptor is not null)
            services.Remove(descriptor);
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(FakeAuthBehaviour<,>));
    }

}
