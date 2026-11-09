using k8s;
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Application;
using Pole.Features.ComputeProfiles.Application.Validation;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.ComputeProfiles.Migrations;
using Pole.Features.DeploymentTargets.Application.Admission;
using Pole.Features.DeploymentTargets.Application.Catalog;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.DeploymentTargets.Migrations;
using Pole.Features.Functions.Application.Common;
using Pole.Features.StorageProfiles.Domain;
using Pole.Tests.Integration.ComputeProfiles;
using Pole.Tests.Integration.DeploymentTargets;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

internal static class NamedCatalogTestData
{
    public const string TargetName = "lab";
    public const string ProfileName = "small";
    public const string RedisStorageProfileId = "507f1f77bcf86cd799439096";
    public const string PostgresStorageProfileId = "507f1f77bcf86cd799439095";

    public static void ConfigureIsolatedApi(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(item =>
                     item.ServiceType == typeof(FenceDeploymentTargetWorkloadConfigurations) &&
                     item.Lifetime == ServiceLifetime.Singleton).ToArray())
            services.Remove(descriptor);

        services.RemoveAll<LookupDeploymentTarget>();
        services.AddSingleton<LookupDeploymentTarget>(provider =>
            provider.GetRequiredService<MongoLookupDeploymentTarget>().InvokeAsync);
        services.RemoveAll<AdmitDeploymentTargetForWorkload>();
        services.AddScoped<AdmitDeploymentTargetForWorkload>(provider =>
            provider.GetRequiredService<MongoAdmitDeploymentTargetForWorkload>().InvokeAsync);
        services.RemoveAll<AdmitDeploymentTargetForNewWorkload>();
        services.AddScoped<AdmitDeploymentTargetForNewWorkload>(provider =>
            provider.GetRequiredService<MongoAdmitDeploymentTargetForNewWorkload>().InvokeAsync);

        services.RemoveAll<ListEligibleDeploymentTargetsForNewWorkload>();
        services.AddScoped<ListEligibleDeploymentTargetsForNewWorkload>(provider =>
            provider.GetRequiredService<MongoListEligibleDeploymentTargetsForNewWorkload>().InvokeAsync);

        services.RemoveAll<ValidateComputeProfileTargetMappings>();
        services.AddSingleton<ValidateComputeProfileTargetMappings>((_, _) =>
            Task.FromResult<ValidateComputeProfileTargetMappingsOutcome>(new ComputeProfileTargetMappingsValid()));
        services.RemoveAll<IKubernetesClientProvider>();
        services.AddSingleton<IKubernetesClientProvider, NoExternalClients>();
    }

    private sealed class NoExternalClients : IKubernetesClientProvider
    {
        private static Task<IKubernetes> Rejected() => Task.FromException<IKubernetes>(
            new InvalidOperationException("Catalog API tests must not request a Kubernetes client."));
        public Task<IKubernetes> GetAsync(string id, CancellationToken ct = default) => Rejected();
        public Task<IKubernetes> GetForCleanupAsync(string id, CancellationToken ct = default) => Rejected();
        public Task<IKubernetes> GetDefaultAsync(CancellationToken ct = default) => Rejected();
    }

    public static async Task SeedAsync(TestDatabaseScope fixture)
    {
        var profiles = fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles");
        var profile = await profiles
            .Find(item => item.Id == TestData.DefaultComputeProfileId).SingleAsync();
        Assert.Equal(ProfileName, profile.TechnicalName);
        profile = profile with
        {
            AllowedResourceTypes = profile.AllowedResourceTypes
                .Add("pole/redis").Add("pole/postgresql").Add("pole/function-app")
        };
        await profiles.ReplaceOneAsync(item => item.Id == profile.Id, profile);
        await fixture.Database.GetCollection<ComputeProfileRevision>("ComputeProfileRevisions").ReplaceOneAsync(
            item => item.Id == $"{profile.Id}/{profile.Revision}",
            ComputeProfileRevisionFactory.Create(profile),
            new ReplaceOptions { IsUpsert = true });
        var target = TestData.SeedDeploymentTarget() with
        {
            TechnicalName = TargetName,
            WorkloadConfiguration = DeploymentTargetWorkloadConfiguration.Create(null,
                new(ImmutableDictionary<string, DeploymentTargetNetworkSource>.Empty.Add(
                    FunctionResourceDeployment.GatewaySourceName,
                    DeploymentTargetCidrNetworkSource.Create("192.0.2.9/32"))))
        };
        await fixture.Database.GetCollection<DeploymentTarget>("DeploymentTargets").ReplaceOneAsync(
            item => item.Id == TestData.DefaultDeploymentTargetId,
            target.AttestForTests(),
            new ReplaceOptions { IsUpsert = true });
        foreach (var (id, name, resourceType) in new[]
                 {
                     (RedisStorageProfileId, "redis-storage", "pole/redis"),
                     (PostgresStorageProfileId, "postgres-storage", "pole/postgresql")
                 })
        {
            var storage = TestData.SeedStorageProfile(id, isDefault: false) with
            {
                TechnicalName = name,
                AllowedResourceTypes = [resourceType]
            };
            await fixture.DbContext.StorageProfiles.InsertOneAsync(storage);
            await fixture.Database.GetCollection<StorageProfileRevision>("StorageProfileRevisions")
                .InsertOneAsync(StorageProfileRevisionFactory.Create(storage));
        }
    }
}
