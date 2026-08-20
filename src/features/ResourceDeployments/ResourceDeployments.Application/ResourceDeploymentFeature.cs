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
        services.TryAddScoped<ResolveResourceDeploymentAdapter>(serviceProvider =>
        {
            var catalog = serviceProvider
                .GetRequiredService<ResourceDeploymentAdapterCatalog>();
            return resourceType =>
            {
                var descriptor = catalog.GetRequired(resourceType);
                var adapter = serviceProvider.GetRequiredService(descriptor.AdapterType);
                if (adapter is not IResourceDeploymentAdapter typedAdapter)
                {
                    throw new InvalidOperationException(
                        $"Registered resource deployment adapter '{descriptor.AdapterType.FullName}' does not implement {nameof(IResourceDeploymentAdapter)}.");
                }

                return (typedAdapter, descriptor.PlacementCapability);
            };
        });
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
        services.TryAddScoped<ResourceDeploymentLegacyAdapters>();
        services.TryAddScoped<EnsureNoUnfinishedResourceGroupDeploymentsLegacy>(provider =>
            provider.GetRequiredService<ResourceDeploymentLegacyAdapters>()
                .EnsureNoUnfinishedForResourceGroupAsync);
        services.TryAddScoped<EnsureNoUnfinishedIsolationScopeDeploymentsLegacy>(provider =>
            provider.GetRequiredService<ResourceDeploymentLegacyAdapters>()
                .EnsureNoUnfinishedForIsolationScopeAsync);
        services.TryAddScoped<EnsureComputeProfileUnreferencedLegacy>(provider =>
            provider.GetRequiredService<ResourceDeploymentLegacyAdapters>()
                .EnsureComputeProfileUnreferencedAsync);
        services.TryAddScoped<LoadCurrentResourceDeploymentComputeLegacy>(provider =>
            provider.GetRequiredService<ResourceDeploymentLegacyAdapters>()
                .LoadCurrentComputeAsync);
    }

    private static void AddResourceDeploymentReconciliationCapabilities(
        this IServiceCollection services)
    {
        services.TryAddScoped<ResourceDeploymentReconciliationPersistence>();
        services.TryAddScoped<ClaimResourceDeployment>(provider =>
            provider.GetRequiredService<ResourceDeploymentReconciliationPersistence>()
                .ClaimAsync);
        services.TryAddScoped<PersistResourceDeploymentSuccess>(provider =>
            provider.GetRequiredService<ResourceDeploymentReconciliationPersistence>()
                .PersistSuccessAsync);
        services.TryAddScoped<PersistResourceDeploymentFailure>(provider =>
            provider.GetRequiredService<ResourceDeploymentReconciliationPersistence>()
                .PersistFailureAsync);

        services.TryAddScoped<ResourceDeploymentRecoveryPersistence>();
        services.TryAddScoped<LoadResourceDeploymentRecoveryCandidates>(provider =>
            provider.GetRequiredService<ResourceDeploymentRecoveryPersistence>()
                .LoadCandidatesAsync);
        services.TryAddScoped<RecoverResourceDeployment>(provider =>
            provider.GetRequiredService<ResourceDeploymentRecoveryPersistence>()
                .RecoverAsync);

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
