using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;

namespace Pole.Features.ComputeProfiles.Application.Persistence;

public static class ComputeProfilePersistenceRegistration
{
    internal const string CollectionName = "ComputeProfiles";

    public static IServiceCollection AddComputeProfilePersistence(
        this IServiceCollection services)
    {
        services.TryAddSingleton(serviceProvider =>
            serviceProvider
                .GetRequiredService<IMongoDatabase>()
                .GetCollection<ComputeProfile>(CollectionName));

        services.AddCapability<MongoLoadComputeProfileById, LoadComputeProfileById>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadDefaultComputeProfile, LoadDefaultComputeProfile>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadComputeProfilePage, LoadComputeProfilePage>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadAvailableComputeProfiles, LoadAvailableComputeProfiles>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoPersistCreatedComputeProfile, PersistCreatedComputeProfile>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoPersistUpdatedComputeProfile, PersistUpdatedComputeProfile>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoDeleteComputeProfile, DeleteComputeProfile>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoResolveComputeProfile, ResolveComputeProfile>(
            implementation => implementation.InvokeAsync);
        services.AddScoped<MongoFenceResolvedComputeProfile>();
        services.AddScoped<IComputeProfileResolutionFence>(serviceProvider =>
            serviceProvider.GetRequiredService<MongoFenceResolvedComputeProfile>());

        services.TryAddSingleton<RunMongoTransaction<ComputeProfile>>(serviceProvider =>
            serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeAsync<ComputeProfile>);
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
