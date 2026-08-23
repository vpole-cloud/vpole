using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Persistence;

public static class DeploymentTargetPersistenceRegistration
{
    public static IServiceCollection AddDeploymentTargetPersistence(
        this IServiceCollection services)
    {
        services.TryAddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IMongoDatabase>()
            .GetCollection<DeploymentTarget>("DeploymentTargets"));
        services.TryAddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IMongoDatabase>()
            .GetCollection<DeploymentTargetReference>("DeploymentTargetReferences"));
        services.TryAddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IMongoDatabase>()
            .GetCollection<DeploymentTargetSettings>("DeploymentTargetSettings"));
        services.TryAddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IMongoDatabase>()
            .GetCollection<DeploymentTargetPublicBaseDomainAdmissionFence>(
                DeploymentTargetPublicBaseDomainAdmissionFence.CollectionName));

        AddCapability<MongoLoadDeploymentTargetById, LoadDeploymentTargetById>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoLoadDefaultDeploymentTargetId, LoadDefaultDeploymentTargetId>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoLoadDeploymentTargets, LoadDeploymentTargets>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoLoadEligibleDeploymentTargets, LoadEligibleDeploymentTargets>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoPersistCreatedDeploymentTarget, PersistCreatedDeploymentTarget>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoPersistUpdatedDeploymentTarget, PersistUpdatedDeploymentTarget>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoPersistRotatedDeploymentTargetConnection,
            PersistRotatedDeploymentTargetConnection>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoPersistDeploymentTargetValidation,
            PersistDeploymentTargetValidation>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoEnsureNoDeploymentTargetReferences,
            EnsureNoDeploymentTargetReferences>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoDeleteDeploymentTarget, DeleteDeploymentTarget>(
            services,
            implementation => implementation.InvokeAsync);

        services.TryAddSingleton<RunMongoOutcomeTransaction<Result<
            DeploymentTarget,
            PersistCreatedDeploymentTargetError>>>(serviceProvider =>
            serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<Result<
                    DeploymentTarget,
                    PersistCreatedDeploymentTargetError>>);
        services.TryAddSingleton<RunMongoOutcomeTransaction<Result<
            DeploymentTarget,
            PersistUpdatedDeploymentTargetError>>>(serviceProvider =>
            serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<Result<
                    DeploymentTarget,
                    PersistUpdatedDeploymentTargetError>>);
        services.TryAddSingleton<RunMongoOutcomeTransaction<DeleteDeploymentTargetOutcome>>(
            serviceProvider => serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<DeleteDeploymentTargetOutcome>);
        return services;
    }

    private static void AddCapability<TImplementation, TDelegate>(
        IServiceCollection services,
        Func<TImplementation, TDelegate> bind)
        where TImplementation : class
        where TDelegate : Delegate
    {
        services.AddScoped<TImplementation>();
        services.AddScoped(serviceProvider =>
            bind(serviceProvider.GetRequiredService<TImplementation>()));
    }
}
