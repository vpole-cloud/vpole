using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.ASP.Helpers;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Application.Persistence;
using Pole.Features.StorageProfiles.Application.Validation;
using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.StorageProfiles.Application;

public static class StorageProfileApplicationRegistration
{
    public static IServiceCollection AddStorageProfilesFeature(
        this IServiceCollection services)
    {
        services.AddHttpProblemMappings(StorageProfileHttpMappings.All);
        services.AddStorageProfilePersistence();

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
