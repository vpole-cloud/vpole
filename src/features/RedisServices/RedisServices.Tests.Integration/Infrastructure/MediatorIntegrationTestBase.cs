// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pole.Database;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.RedisServices.Application;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Events;
using Pole.Features.RedisServices.Migrations;
using Pole.Features.ResourceConnections.Application;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.Tenants.Testing;
using Pole.Mediator;
using Pole.Platform.Hosting;
using Xunit;

namespace Pole.Features.RedisServices.Tests.Integration.Infrastructure;

[Collection(IntegrationTestCollection.Name)]
public abstract class MediatorIntegrationTestBase(
    MongoReplicaSetFixture fixture
) : IAsyncLifetime
{
    private readonly TestEncryptionSettings _encryption = new();
    private IServiceScope? _scope;
    private ServiceProvider? _serviceProvider;

    protected MongoReplicaSetFixture Fixture { get; } = fixture;
    protected RedisTestStore DbContext { get; private set; } = null!;
    protected IMediator Mediator => _scope!.ServiceProvider.GetRequiredService<IMediator>();
    protected IServiceProvider Services => _scope!.ServiceProvider;

    public virtual async Task InitializeAsync()
    {
        await Fixture.ResetDatabaseAsync();
        DbContext = new RedisTestStore(Fixture.Client, Fixture.Database);

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
            [typeof(RedisServiceEndpoints).Assembly],
            [typeof(RedisServiceCreatedEvent).Assembly],
            []);
        services.AddRedisServicesFeature();
        services.AddResourceConnectionsFeature();
        services.AddFeatureMigrations("pole.redis", typeof(RedisSchemaValidationMigration).Assembly);
        services.AddTestDeploymentTargets();
        services.AddUnavailableSourceBuildTargets();
        ReplaceTenantAuthorizationWithFake(services);
        _serviceProvider = services.BuildServiceProvider(validateScopes: true);
        await _serviceProvider.InitializePolePlatformAsync();

        await DbContext.StorageProfiles.InsertOneAsync(RedisTestData.SeedStorageProfile());
        await Fixture.Database
            .GetCollection<ComputeProfile>("ComputeProfiles")
            .InsertOneAsync(RedisTestData.SeedComputeProfile());
        await new TenantTestData(Fixture.Database).CreateAsync("Test tenant", RedisTestData.TenantId);
        _scope = _serviceProvider.CreateScope();
    }

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
