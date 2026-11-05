using MongoDB.Driver;
using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.Features.ComputeProfiles.Migrations;
using Pole.Features.ComputeProfiles.Application;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.ComputeProfiles.Application.Validation;
using Pole.Features.DeploymentTargets.Migrations;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Application.Admission;
using Pole.Features.DeploymentTargets.Application.Catalog;
using Pole.Tests.Integration.ComputeProfiles;
using Pole.Tests.Integration.DeploymentTargets;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

internal static class NamedCatalogTestData
{
    public const string TargetName = "lab";
    public const string ProfileName = "small";

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

    public static async Task SeedAsync(MongoReplicaSetFixture fixture)
    {
        await new ComputeProfileIndexesMigration().Up(fixture.Database);
        await new DeploymentTargetMigration().Up(fixture.Database);
        var profile = await fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles")
            .Find(item => item.Id == TestData.DefaultComputeProfileId).SingleAsync();
        Assert.Equal(ProfileName, profile.TechnicalName);
        await fixture.Database.GetCollection<ComputeProfileRevision>("ComputeProfileRevisions").ReplaceOneAsync(
            item => item.Id == $"{profile.Id}/{profile.Revision}",
            ComputeProfileRevisionFactory.Create(profile),
            new ReplaceOptions { IsUpsert = true });
        await fixture.Database.GetCollection<DeploymentTarget>("DeploymentTargets").ReplaceOneAsync(
            item => item.Id == TestData.DefaultDeploymentTargetId,
            TestData.SeedDeploymentTarget().AttestForTests() with { TechnicalName = TargetName },
            new ReplaceOptions { IsUpsert = true });
    }
}
