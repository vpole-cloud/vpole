using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Backups.Contracts;
using Pole.Database;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.Postgres.Application;
using Pole.Features.Postgres.Application.Endpoints;
using Pole.Features.Postgres.Events;
using Pole.Features.Postgres.Migrations;
using Pole.Features.ResourceConnections.Application;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.Tenants.Testing;
using Pole.Mediator;
using Pole.Platform.Hosting;
using Xunit;

namespace Pole.Features.Postgres.Tests.Integration.Infrastructure;

[Collection(IntegrationTestCollection.Name)]
public abstract class MediatorIntegrationTestBase(
    MongoReplicaSetFixture fixture
) : IAsyncLifetime
{
    private readonly TestEncryptionSettings _encryption = new();
    private IServiceScope? _scope;
    private ServiceProvider? _serviceProvider;

    protected MongoReplicaSetFixture Fixture { get; } = fixture;
    protected PostgresTestStore DbContext { get; private set; } = null!;
    protected IMediator Mediator => _scope!.ServiceProvider.GetRequiredService<IMediator>();
    protected IServiceProvider Services => _scope!.ServiceProvider;

    public virtual async Task InitializeAsync()
    {
        await Fixture.ResetDatabaseAsync();
        DbContext = new PostgresTestStore(Fixture.Client, Fixture.Database);

        var configuration = new ConfigurationBuilder().AddConfiguration(_encryption.Configuration)
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["kubernetes:NamespaceNamePrefix"] = "test",
                })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(Fixture.Client);
        services.AddSingleton(Fixture.Database);
        services.AddSingleton<MongoRunTransaction>();
        var storageDiscovery = new TestStorageDiscoveryService("standard", "fast", "bulk");
        services.AddSingleton(storageDiscovery);
        services.AddSingleton<DiscoverKubernetesStorageClasses>(storageDiscovery.GetStorageClassesAsync);
        services.AddSingleton<DiscoverKubernetesStorageClassNames>(storageDiscovery.GetStorageClassNamesAsync);
        services.AddSingleton<DiscoverKubernetesPersistentVolumes>(storageDiscovery.GetPersistentVolumesAsync);
        services.AddSingleton<DiscoverKubernetesPersistentVolumeClaims>(
            storageDiscovery.GetPersistentVolumeClaimsAsync);
        services.AddPolePlatform(
            configuration,
            [typeof(PostgresClusterEndpoints).Assembly],
            [typeof(PostgresClusterCreatedEvent).Assembly],
            []);
        services.AddPostgresFeature(configuration);
        services.AddSingleton<RecordRestoreTargetCreation>((
            _, _,
            _) => Task.FromResult(RestoreTargetCreationDecision.OperationUnavailable));
        services.AddFeatureMigrations("pole.postgres", typeof(PostgresNameIndexesMigration).Assembly);
        services.AddResourceConnectionsFeature();
        services.AddTestDeploymentTargets();
        services.AddUnavailableSourceBuildTargets();
        ReplaceTenantAuthorizationWithFake(services);
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider(validateScopes: true);
        await _serviceProvider.InitializePolePlatformAsync();

        await DbContext.StorageProfiles.InsertOneAsync(PostgresTestData.SeedStorageProfile());
        await Fixture.Database
            .GetCollection<ComputeProfile>("ComputeProfiles")
            .InsertOneAsync(PostgresTestData.SeedComputeProfile());
        await new TenantTestData(Fixture.Database).CreateAsync("Test tenant", PostgresTestData.TenantId);
        _scope = _serviceProvider.CreateScope();
    }

    public virtual Task DisposeAsync()
    {
        _scope?.Dispose();
        _serviceProvider?.Dispose();
        _encryption.Dispose();
        return Task.CompletedTask;
    }

    protected virtual void ConfigureServices(IServiceCollection services)
    {
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
