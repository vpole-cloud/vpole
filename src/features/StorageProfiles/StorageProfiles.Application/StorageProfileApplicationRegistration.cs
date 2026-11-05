using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pole.Features.StorageProfiles.Application.Observation;
using Pole.Features.StorageProfiles.Application.Discovery.Observation;
using Pole.Features.StorageProfiles.Application.Realtime;
using Pole.ASP.Helpers;
using Pole.Features.Realtime.Contracts;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Application.Validation;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain.Observation;

namespace Pole.Features.StorageProfiles.Application;

public static class StorageProfileApplicationRegistration
{
    public static IServiceCollection AddStorageProfilesFeature(
        this IServiceCollection services)
    {
        services.AddHttpProblemMappings(StorageProfileHttpMappings.All);
        services.AddStorageProfilePersistence();
        services.AddSingleton(new RealtimeTopic("storage-profiles", RealtimeScope.Platform));
        services.AddSingleton(new RealtimeTopic("storage-eligibility", RealtimeScope.Tenant));
        services.AddSingleton(new RealtimeTopic("storage-observations", RealtimeScope.Platform));
        services.AddOptions<StorageObservationOptions>().Configure<IServiceProvider>((options, provider) =>
            {
                var configuration = provider.GetService<IConfiguration>();
                configuration?.GetSection(StorageObservationOptions.SectionName).Bind(options);
                options.Enabled = options.Enabled && configuration?.GetValue<bool>("Realtime:Enabled") == true;
            }).Validate(options => options.Valid, "Storage observation limits are outside the supported bounds.")
            .ValidateOnStart();
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<StorageObservationOptions>>().Value);
        services.TryAddSingleton(provider => provider.GetRequiredService<IMongoDatabase>()
            .GetCollection<StorageObservationControlDocument>(MongoStorageObservationStore.ControlsCollection));
        services.TryAddSingleton(provider => provider.GetRequiredService<IMongoDatabase>()
            .GetCollection<StorageObservationDemandDocument>(MongoStorageObservationStore.DemandsCollection));
        services.TryAddSingleton(provider => provider.GetRequiredService<IMongoDatabase>()
            .GetCollection<StorageObservationSnapshotDocument>(MongoStorageObservationStore.SnapshotsCollection));
        services.TryAddScoped<IStorageObservationStore, MongoStorageObservationStore>();
        services.TryAddScoped<StorageObservationContextLoader>();
        services.TryAddScoped<StorageObservationNativeReader>();
        services.TryAddSingleton<StorageObservationMetrics>();
        services.TryAddScoped<StorageObservationReader>();
        services.TryAddSingleton<StorageObservationWakeup>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IRealtimeTopicLifecycle, StorageObservationLifecycle>());
        services.TryAddScoped<IStorageObservationSource, KubernetesStorageObservationSource>();
        services.AddHostedService<StorageObservationCoordinator>();

        services.AddCapability<KubernetesDiscoverStorageClasses,
            DiscoverKubernetesStorageClasses>(implementation => implementation.InvokeAsync);
        services.AddCapability<KubernetesDiscoverStorageClassNames,
            DiscoverKubernetesStorageClassNames>(implementation => implementation.InvokeAsync);
        services.AddCapability<KubernetesDiscoverPersistentVolumes,
            DiscoverKubernetesPersistentVolumes>(implementation => implementation.InvokeAsync);
        services.AddCapability<KubernetesDiscoverPersistentVolumeClaims,
            DiscoverKubernetesPersistentVolumeClaims>(implementation => implementation.InvokeAsync);
        services.AddCapability<KubernetesStorageProfileTargetMappingValidator,
            ValidateStorageProfileTargetMappings>(implementation => implementation.InvokeAsync);
        return services;
    }

    private static void AddCapability<TImplementation, TDelegate>(
        this IServiceCollection services,
        Func<TImplementation, TDelegate> bind)
        where TImplementation : class
        where TDelegate : Delegate
    {
        services.TryAddScoped<TImplementation>();
        services.TryAddScoped(serviceProvider =>
            bind(serviceProvider.GetRequiredService<TImplementation>()));
    }
}
