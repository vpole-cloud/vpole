using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.StorageProfiles.Application.Catalog;
using Pole.Features.StorageProfiles.Application.Resolution;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain;

namespace Pole.Features.StorageProfiles.Application;

public static class StorageProfilePersistenceRegistration
{
    private const string CollectionName = "StorageProfiles";

    public static IServiceCollection AddStorageProfilePersistence(this IServiceCollection services)
    {
        services.TryAddSingleton(serviceProvider =>
            serviceProvider.GetRequiredService<IMongoDatabase>().GetCollection<StorageProfile>(CollectionName));

        services.TryAddSingleton(serviceProvider => serviceProvider.GetRequiredService<IMongoDatabase>()
            .GetCollection<StorageProfileRevision>("StorageProfileRevisions"));
        services.AddScoped<StorageProfileHistory>();
        services.AddCapability<MongoResolveNamedStorageProfile, ResolveNamedStorageProfile>(implementation =>
            implementation.InvokeAsync);
        services
            .AddCapability<MongoStorageProfilePlanningSnapshots,
                ResolveStorageProfilePlanningSnapshot>(implementation => implementation.ReadAsync);
        services.AddScoped<FenceStorageProfilePlanningSnapshot>(provider =>
            provider.GetRequiredService<MongoStorageProfilePlanningSnapshots>().FenceAsync);

        services.AddCapability<MongoLoadStorageProfileById, LoadStorageProfileById>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoLoadStorageProfiles, LoadStorageProfiles>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoLoadAvailableStorageProfiles, LoadAvailableStorageProfiles>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoPersistCreatedStorageProfile, PersistCreatedStorageProfile>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoPersistUpdatedStorageProfile, PersistUpdatedStorageProfile>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoResolveStorageProfile, ResolveStorageProfile>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoFenceStorageProfileResolution, FenceStorageProfileResolution>(implementation =>
            implementation.InvokeAsync);

        services.TryAddSingleton<RunMongoOutcomeTransaction<PersistCreatedStorageProfileOutcome>>(serviceProvider =>
            serviceProvider.GetRequiredService<MongoRunTransaction>().InvokeOutcomeAsync);
        services.TryAddSingleton<RunMongoOutcomeTransaction<PersistUpdatedStorageProfileOutcome>>(serviceProvider =>
            serviceProvider.GetRequiredService<MongoRunTransaction>().InvokeOutcomeAsync);
        return services;
    }

    private static void AddCapability<TImplementation, TDelegate>(
        this IServiceCollection services,
        Func<TImplementation, TDelegate> bind)
        where TImplementation : class
        where TDelegate : Delegate
    {
        services.AddScoped<TImplementation>();
        services.AddScoped(serviceProvider => bind(serviceProvider.GetRequiredService<TImplementation>()));
    }
}
