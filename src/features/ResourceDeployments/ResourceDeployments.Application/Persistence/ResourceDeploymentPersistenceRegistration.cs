using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Application.Endpoints;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Persistence;

internal static class ResourceDeploymentPersistenceRegistration
{
    internal static IServiceCollection AddResourceDeploymentPersistence(
        this IServiceCollection services)
    {
        services.TryAddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IMongoDatabase>()
            .GetCollection<ResourceDeployment>("ResourceDeployments"));
        services.TryAddSingleton<RunMongoOutcomeTransaction<long>>(serviceProvider =>
            serviceProvider.GetRequiredService<MongoRunTransaction>().InvokeOutcomeAsync<long>);
        services.TryAddSingleton<RunMongoOutcomeTransaction<Unit>>(serviceProvider =>
            serviceProvider.GetRequiredService<MongoRunTransaction>().InvokeOutcomeAsync<Unit>);
        services.TryAddSingleton<RunMongoOutcomeTransaction<RetryResourceDeploymentOutcome>>(
            serviceProvider => serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<RetryResourceDeploymentOutcome>);

        AddCapability<MongoCreateResourceDeployment, CreateResourceDeployment>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoCreateResourceDeploymentSet, CreateResourceDeploymentSet>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoRequestResourceDeploymentApply, RequestResourceDeploymentApply>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoRequestResourceDeploymentDeletion, RequestResourceDeploymentDeletion>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoRequestExactResourceDeploymentApply,
            RequestExactResourceDeploymentApply>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoRequestExactResourceDeploymentDeletion,
            RequestExactResourceDeploymentDeletion>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoRequestResourceDeploymentSetApply,
            RequestResourceDeploymentSetApply>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoRequestResourceDeploymentSetDeletion,
            RequestResourceDeploymentSetDeletion>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoAdvanceResourceDeploymentRevision, AdvanceResourceDeploymentRevision>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoRetryExactResourceDeployment, RetryExactResourceDeployment>(
            services,
            implementation => implementation.InvokeAsync);

        AddCapability<MongoLoadResourceDeployment, LoadResourceDeployment>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoLoadResourceDeployments, LoadResourceDeployments>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoLoadResourceDeploymentSet, LoadResourceDeploymentSet>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoLoadExactResourceDeployment, LoadExactResourceDeployment>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoLoadResourceDeploymentSets, LoadResourceDeploymentSets>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoLoadCurrentResourceDeploymentCompute,
            LoadCurrentResourceDeploymentCompute>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoEnsureComputeProfileUnreferenced, EnsureComputeProfileUnreferenced>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoEnsureNoUnfinishedResourceGroupDeployments,
            EnsureNoUnfinishedResourceGroupDeployments>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoEnsureNoUnfinishedIsolationScopeDeployments,
            EnsureNoUnfinishedIsolationScopeDeployments>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoGetResourceDeploymentFinalizationReadiness,
            GetResourceDeploymentFinalizationReadiness>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoPurgeDeletedResourceDeployments, PurgeDeletedResourceDeployments>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoGetExactResourceDeploymentFinalizationReadiness,
            GetExactResourceDeploymentFinalizationReadiness>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoPurgeExactDeletedResourceDeployment,
            PurgeExactDeletedResourceDeployment>(
            services,
            implementation => implementation.InvokeAsync);

        AddCapability<ClaimResourceDeploymentOperation, ClaimResourceDeployment>(
            services,
            implementation => implementation.InvokeAsync);
        services.TryAddScoped<ResourceDeploymentCompletionPersistence>();
        AddCapability<PersistResourceDeploymentSuccessOperation, PersistResourceDeploymentSuccess>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<PersistResourceDeploymentFailureOperation, PersistResourceDeploymentFailure>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<LoadResourceDeploymentRecoveryCandidatesOperation,
            LoadResourceDeploymentRecoveryCandidates>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<RecoverResourceDeploymentOperation, RecoverResourceDeployment>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<ResourceDeploymentWakePersistence, WakeResourceDeploymentsForRuntimeCell>(
            services,
            implementation => implementation.WakeAsync);
        AddCapability<ResourceDeploymentRetryPersistence, RetryResourceDeployment>(
            services,
            implementation => implementation.RetryAsync);
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
