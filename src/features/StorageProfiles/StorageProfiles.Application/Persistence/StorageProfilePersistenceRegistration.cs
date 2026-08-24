using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Persistence;

public static class StorageProfilePersistenceRegistration
{
    private const string CollectionName = "StorageProfiles";

    public static IServiceCollection AddStorageProfilePersistence(
        this IServiceCollection services)
    {
        services.TryAddSingleton(serviceProvider =>
            serviceProvider
                .GetRequiredService<IMongoDatabase>()
                .GetCollection<StorageProfile>(CollectionName));

        services.AddCapability<MongoLoadStorageProfileById, LoadStorageProfileById>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadStorageProfiles, LoadStorageProfiles>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadAvailableStorageProfiles, LoadAvailableStorageProfiles>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoPersistCreatedStorageProfile, PersistCreatedStorageProfile>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoPersistUpdatedStorageProfile, PersistUpdatedStorageProfile>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoResolveStorageProfile, ResolveStorageProfile>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoFenceStorageProfileResolution,
            FenceStorageProfileResolution>(implementation => implementation.InvokeAsync);

        services.TryAddSingleton<RunMongoOutcomeTransaction<StorageProfile>>(
            serviceProvider => serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<StorageProfile>);
        services.TryAddSingleton<RunMongoOutcomeTransaction<Result<
            StorageProfile,
            StorageProfileRevisionConflict>>>(serviceProvider => serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<Result<
                    StorageProfile,
                    StorageProfileRevisionConflict>>);
        return services;
    }

    private static void AddCapability<TImplementation, TDelegate>(
        this IServiceCollection services,
        Func<TImplementation, TDelegate> bind)
        where TImplementation : class
        where TDelegate : Delegate
    {
        services.AddScoped<TImplementation>();
        services.AddScoped(serviceProvider =>
            bind(serviceProvider.GetRequiredService<TImplementation>()));
    }
}
