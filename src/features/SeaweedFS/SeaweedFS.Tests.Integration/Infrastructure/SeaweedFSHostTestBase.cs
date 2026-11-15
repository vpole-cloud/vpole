using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Pole.Mediator;
using Pole.Features.ResourceConnections.Application.Endpoints;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.Postgres.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.ResourceConnections.Application;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Testing;
using Pole.Features.SeaweedFS.Application;
using Pole.Features.SeaweedFS.Application.Common;
using Pole.Features.SeaweedFS.Application.Endpoints;
using Pole.Features.SeaweedFS.Application.Lifecycle;
using Pole.Features.SeaweedFS.Events;
using Pole.Features.SeaweedFS.Migrations;
using Pole.Features.SeaweedFS.Tests.Integration.Infrastructure;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Domain;
using Pole.Platform.Hosting;
using Pole.Testing;
using Xunit;

namespace Pole.Features.SeaweedFS.Tests.Integration;

public abstract class SeaweedFSHostTestBase(
    MongoReplicaSetFixture fixture
) : IAsyncLifetime
{
    protected MongoReplicaSetFixture Fixture { get; } = fixture;
    protected TestClock Clock { get; } = new();
    protected const string Route = "/api/v1/seaweedfs/";
    protected static readonly System.Text.Json.JsonSerializerOptions Json = PoleHttpJson.CreateSerializerOptions();
    public Task InitializeAsync() => Fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    protected async Task<FeatureTestHost> StartAsync(Action<IServiceCollection>? configure = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["SeaweedFS:DeletionRetention"] = "00:00:01"
                })
            .Build();
        var host = await FeatureTestHost.StartAsync(
            configuration,
            typeof(SeaweedFSFeatureRegistration).Assembly,
            typeof(SeaweedFSServiceCreatedEvent).Assembly,
            (services, _) => services.AddSeaweedFSFeature().AddResourceConnectionsFeature(),
            endpoints =>
            {
                endpoints.MapSeaweedFSEndpoints();
                endpoints.MapGroup("/resource-connections").MapGetResourceConnectionEndpointsEndpoint();
            },
            null,
            services =>
            {
                services.AddFeatureMigrations("pole.seaweedfs", typeof(SeaweedFSSchemaMigration).Assembly);
                services
                    .AddScoped<IMediatorOutcomeHandler<GetResourceConnectionEndpointsRequest,
                        GetResourceConnectionEndpointsOutcome>, GetResourceConnectionEndpointsHandler>();
                services
                    .AddScoped<IValidator<GetResourceConnectionEndpointsRequest>,
                        GetResourceConnectionEndpointsValidator>();
                services.AddSingleton<TimeProvider>(Clock);
                services.AddSingleton(Fixture.Client);
                services.AddSingleton(Fixture.Database);
                services.AddScoped<EnsureRuntimeCellIngressBaseline>(_ => (_, _) =>
                    Task.FromResult<EnsureRuntimeCellIngressBaselineOutcome>(new RuntimeCellIngressBaselineReady()));
                services.AddScoped<ClaimPostgresManagedDatabase>(_ => (
                    _, _,
                    _) => Task.FromResult<ClaimPostgresManagedDatabaseOutcome>(
                    new PostgresManagedDatabaseUnavailable()));
                services.AddScoped<EnsurePostgresManagedDatabase>(_ => (_, _) =>
                    Task.FromResult<EnsurePostgresManagedDatabaseOutcome>(new PostgresManagedDatabaseUnavailable()));
                services.AddScoped<PurgePostgresManagedDatabase>(_ => (_, _) =>
                    Task.FromResult<PurgePostgresManagedDatabaseOutcome>(new PostgresManagedDatabasePurged()));
                services.AddScoped<PurgeSeaweedFSBucketData>(_ => (_, _) =>
                    Task.FromResult<PurgeSeaweedFSBucketDataOutcome>(new SeaweedFSBucketDataPurged()));
                services.AddTestDeploymentTargets();
                var storage = new TestStorageDiscoveryService("standard");
                services.AddSingleton<DiscoverKubernetesStorageClasses>(storage.GetStorageClassesAsync);
                services.AddSingleton<DiscoverKubernetesStorageClassNames>(storage.GetStorageClassNamesAsync);
                services.AddSingleton<DiscoverKubernetesPersistentVolumes>(storage.GetPersistentVolumesAsync);
                services.AddSingleton<DiscoverKubernetesPersistentVolumeClaims>(storage.GetPersistentVolumeClaimsAsync);
                configure?.Invoke(services);
            });
        await host.SeedTenantAsync();
        return host;
    }

    protected async Task<CreateSeaweedFSServiceRequestBody> SeedCreateAsync()
    {
        await Fixture.Database
            .GetCollection<StorageProfile>("StorageProfiles")
            .InsertOneAsync(SeaweedFSTestData.SeedStorageProfile());
        await Fixture.Database
            .GetCollection<ComputeProfile>("ComputeProfiles")
            .InsertOneAsync(SeaweedFSTestData.SeedComputeProfile());
        var group = SeaweedFSTestData.SeedResourceGroup();
        await new ResourceGroupTestData(Fixture.Database).SeedAsync(group);
        return SeaweedFSTestData.CreateSeaweedFSServiceRequest(group.Id).Body;
    }

    protected sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    protected static HttpClient AuthenticatedClient(FeatureTestHost host)
    {
        var client = host.GetClient();
        client.DefaultRequestHeaders.Add(FeatureTestAuthentication.UserHeader, TestPlatformDefaults.UserId);
        client.DefaultRequestHeaders.Add("X-Pole-Tenant-ID", TestPlatformDefaults.TenantId);
        return client;
    }
}
