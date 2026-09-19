using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Persistence.Admission;
using Pole.Features.DeploymentTargets.Application.Persistence.Bootstrap;
using Pole.Features.DeploymentTargets.Application.Persistence.Credentials;
using Pole.Features.DeploymentTargets.Application.Persistence.Mutations;
using Pole.Features.DeploymentTargets.Application.Persistence.Reads;
using Pole.Features.DeploymentTargets.Application.Persistence.References;
using Pole.Features.DeploymentTargets.Contracts;
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

        AddSingletonCapability<MongoLoadDeploymentTargetById, LoadDeploymentTargetById>(
            services,
            implementation => implementation.InvokeAsync);
        AddSingletonCapability<MongoLoadDefaultDeploymentTargetId, LoadDefaultDeploymentTargetId>(
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
        AddCapability<MongoLookupDeploymentTarget, LookupDeploymentTarget>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoLookupDefaultDeploymentTarget, LookupDefaultDeploymentTarget>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoListEligibleDeploymentTargetsForNewWorkload,
            ListEligibleDeploymentTargetsForNewWorkload>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoAdmitDeploymentTargetForWorkload, AdmitDeploymentTargetForWorkload>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoAdmitDefaultDeploymentTargetForNewWorkload,
            AdmitDefaultDeploymentTargetForNewWorkload>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoAdmitDeploymentTargetForNewWorkload,
            AdmitDeploymentTargetForNewWorkload>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoAdmitDeploymentTargetPublicIngressForNewWorkload,
            AdmitDeploymentTargetPublicIngressForNewWorkload>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoListEligiblePublicIngressDeploymentTargetsForNewWorkload,
            ListEligiblePublicIngressDeploymentTargetsForNewWorkload>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoAdmitDeploymentTargetPublicIngressForDomainReservation,
            AdmitDeploymentTargetPublicIngressForDomainReservation>(
            services,
            implementation => implementation.InvokeAsync);
        AddSingletonCapability<MongoEnsureNoOverlappingDeploymentTargetPublicBaseDomain,
            EnsureNoOverlappingDeploymentTargetPublicBaseDomain>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoEnsureNoDeploymentTargetPublicBaseDomainReferences,
            EnsureNoDeploymentTargetPublicBaseDomainReferences>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoApplyDeploymentTargetReferenceChanges,
            ApplyDeploymentTargetReferenceChanges>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoFenceDeploymentTargetWorkloadConfigurations,
            FenceDeploymentTargetWorkloadConfigurations>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoRewrapDeploymentTargetCredentials, RewrapDeploymentTargetCredentials>(
            services,
            implementation => implementation.InvokeAsync);

        services.TryAddSingleton<MongoLoadDeploymentTargetBootstrapState>();
        services.TryAddSingleton<LoadDeploymentTargetBootstrapState>(serviceProvider => serviceProvider
            .GetRequiredService<MongoLoadDeploymentTargetBootstrapState>()
            .InvokeAsync);
        services.TryAddSingleton<MongoEnsureBootstrapDefaultDeploymentTarget>();
        services.TryAddSingleton<EnsureBootstrapDefaultDeploymentTarget>(serviceProvider => serviceProvider
            .GetRequiredService<MongoEnsureBootstrapDefaultDeploymentTarget>()
            .InvokeAsync);

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
        services.TryAddSingleton<RunMongoOutcomeTransaction<DeleteDeploymentTargetPersistenceOutcome>>(
            serviceProvider => serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<DeleteDeploymentTargetPersistenceOutcome>);
        services.TryAddSingleton<RunMongoOutcomeTransaction<EnsureBootstrapDefaultDeploymentTargetOutcome>>(
            serviceProvider => serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<EnsureBootstrapDefaultDeploymentTargetOutcome>);
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

    private static void AddSingletonCapability<TImplementation, TDelegate>(
        IServiceCollection services,
        Func<TImplementation, TDelegate> bind)
        where TImplementation : class
        where TDelegate : Delegate
    {
        services.TryAddSingleton<TImplementation>();
        services.TryAddSingleton(serviceProvider =>
            bind(serviceProvider.GetRequiredService<TImplementation>()));
    }
}
