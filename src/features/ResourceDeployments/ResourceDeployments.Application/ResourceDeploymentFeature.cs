using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Endpoints;
using Pole.Features.ResourceDeployments.Application.Jobs;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.Features.ResourceDeployments.Application.Placement;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application;

public static class ResourceDeploymentFeature
{
    public static IServiceCollection AddResourceDeploymentsFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Contract mappings are composed by the host from the sibling
        // Contracts.HttpMapping adapter project. Application intentionally does
        // not reference that project; see the module dependency policy.
        services.AddHttpProblemMappings(ResourceDeploymentHttpMappings.All);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<ResourceDeploymentSettings>,
                ResourceDeploymentSettingsValidator>());
        services.AddOptions<ResourceDeploymentSettings>()
            .Bind(configuration.GetSection(ResourceDeploymentSettings.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton(serviceProvider =>
            serviceProvider
                .GetRequiredService<IOptions<ResourceDeploymentSettings>>()
                .Value);

        services.TryAddSingleton(serviceProvider =>
            serviceProvider
                .GetRequiredService<IMongoDatabase>()
                .GetCollection<ResourceDeployment>("ResourceDeployments"));
        services.TryAddSingleton<RunMongoTransaction<long>>(serviceProvider =>
            serviceProvider.GetRequiredService<MongoRunTransaction>().InvokeAsync<long>);
        services.TryAddSingleton<RunMongoOutcomeTransaction<
            RetryResourceDeploymentOutcome>>(serviceProvider =>
            serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<RetryResourceDeploymentOutcome>);

        services.AddResourceDeploymentPlacementCapabilities();
        services.AddResourceDeploymentReadCapabilities();
        services.AddResourceDeploymentReconciliationCapabilities();

        services.TryAddSingleton<ResourceDeploymentMetrics>();
        services.TryAddSingleton<ResourceDeploymentAdapterCatalog>();
        services.TryAddScoped<ResourceDeploymentAdapterResolver>();
        services.TryAddScoped<ResolveResourceDeploymentAdapter>(serviceProvider =>
            serviceProvider
                .GetRequiredService<ResourceDeploymentAdapterResolver>()
                .Resolve);
        services.TryAddSingleton<ResourceDeploymentRetryDelayPolicy>();
        services.TryAddScoped<ResourceDeploymentReconciler>();
        services.TryAddScoped<ResourceDeploymentRecoveryJob>();

        return services;
    }

    private static void AddResourceDeploymentReadCapabilities(
        this IServiceCollection services)
    {
        services.TryAddScoped<ResourceDeploymentReferenceLookup>();
        services.TryAddScoped<EnsureNoUnfinishedResourceGroupDeployments>(provider =>
            provider.GetRequiredService<ResourceDeploymentReferenceLookup>()
                .EnsureNoUnfinishedForResourceGroupAsync);
        services.TryAddScoped<EnsureNoUnfinishedIsolationScopeDeployments>(provider =>
            provider.GetRequiredService<ResourceDeploymentReferenceLookup>()
                .EnsureNoUnfinishedForIsolationScopeAsync);
        services.TryAddScoped<ResourceDeploymentComputeProfileReferenceGuard>();
        services.TryAddScoped<EnsureComputeProfileUnreferenced>(provider =>
            provider.GetRequiredService<ResourceDeploymentComputeProfileReferenceGuard>()
                .EnsureUnreferencedAsync);
        services.TryAddScoped<ResourceDeploymentComputeLookup>();
        services.TryAddScoped<LoadCurrentResourceDeploymentCompute>(provider =>
            provider.GetRequiredService<ResourceDeploymentComputeLookup>()
                .GetCurrentAsync);
    }

    private static void AddResourceDeploymentReconciliationCapabilities(
        this IServiceCollection services)
    {
        services.TryAddScoped<ClaimResourceDeploymentOperation>();
        services.TryAddScoped<ClaimResourceDeployment>(provider =>
            provider.GetRequiredService<ClaimResourceDeploymentOperation>()
                .InvokeAsync);
        services.TryAddScoped<ResourceDeploymentCompletionPersistence>();
        services.TryAddScoped<PersistResourceDeploymentSuccessOperation>();
        services.TryAddScoped<PersistResourceDeploymentSuccess>(provider =>
            provider.GetRequiredService<PersistResourceDeploymentSuccessOperation>()
                .InvokeAsync);
        services.TryAddScoped<PersistResourceDeploymentFailureOperation>();
        services.TryAddScoped<PersistResourceDeploymentFailure>(provider =>
            provider.GetRequiredService<PersistResourceDeploymentFailureOperation>()
                .InvokeAsync);

        services.TryAddScoped<LoadResourceDeploymentRecoveryCandidatesOperation>();
        services.TryAddScoped<LoadResourceDeploymentRecoveryCandidates>(provider =>
            provider.GetRequiredService<LoadResourceDeploymentRecoveryCandidatesOperation>()
                .InvokeAsync);
        services.TryAddScoped<RecoverResourceDeploymentOperation>();
        services.TryAddScoped<RecoverResourceDeployment>(provider =>
            provider.GetRequiredService<RecoverResourceDeploymentOperation>()
                .InvokeAsync);

        services.TryAddScoped<ResourceDeploymentWakePersistence>();
        services.TryAddScoped<WakeResourceDeploymentsForRuntimeCell>(provider =>
            provider.GetRequiredService<ResourceDeploymentWakePersistence>()
                .WakeAsync);

        services.TryAddScoped<ResourceDeploymentRetryPersistence>();
        services.TryAddScoped<RetryResourceDeployment>(provider =>
            provider.GetRequiredService<ResourceDeploymentRetryPersistence>()
                .RetryAsync);
    }
}
