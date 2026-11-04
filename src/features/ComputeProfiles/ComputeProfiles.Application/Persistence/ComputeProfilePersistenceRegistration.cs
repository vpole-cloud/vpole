using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Application.Endpoints;
using Pole.Features.ComputeProfiles.Application.Persistence.Mutations;
using Pole.Features.ComputeProfiles.Application.Persistence.Reads;
using Pole.Features.ComputeProfiles.Application.Persistence.Resolution;
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

        services.TryAddSingleton(sp => sp.GetRequiredService<IMongoDatabase>().GetCollection<ComputeProfileRevision>("ComputeProfileRevisions"));
        services.AddScoped<ComputeProfileHistory>();
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
        services.AddCapability<MongoComputeProfilePlanningSnapshots, ResolveComputeProfilePlanningSnapshot>(
            implementation => implementation.ReadAsync);
        services.AddCapability<MongoComputeProfilePlanningSnapshots, FenceComputeProfilePlanningSnapshot>(
            implementation => implementation.FenceAsync);
        services.AddCapability<MongoResolveNamedComputeProfile, ResolveNamedComputeProfile>(implementation => implementation.InvokeAsync);
        services.AddCapability<MongoResolveComputeProfiles, ResolveComputeProfiles>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoResolveComputeProfile, ResolveComputeProfile>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoFenceResolvedComputeProfile,
            FenceResolvedComputeProfiles>(implementation => implementation.InvokeAsync);

        services.TryAddSingleton<RunMongoOutcomeTransaction<PersistCreatedComputeProfileOutcome>>(serviceProvider =>
            serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<PersistCreatedComputeProfileOutcome>);
        services.TryAddSingleton<RunMongoOutcomeTransaction<PersistUpdatedComputeProfileOutcome>>(serviceProvider =>
            serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<PersistUpdatedComputeProfileOutcome>);
        services.TryAddSingleton<RunMongoOutcomeTransaction<DeleteComputeProfileOutcome>>(
            serviceProvider => serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<DeleteComputeProfileOutcome>);
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
