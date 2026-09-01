using Microsoft.Extensions.Configuration;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Kubernetes;
using Pole.Features.ResourceGroups.Application.Jobs;
using Pole.Features.ResourceGroups.Application.Persistence;
using Pole.Features.ResourceGroups.Application.Persistence.IsolationScopes;
using Pole.Features.ResourceGroups.Application.Persistence.Placements;
using Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups;
using Pole.Features.ResourceGroups.Application.Persistence.RuntimeCells;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Application.Search;
using Pole.Features.Search.Contracts;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application;

public static class ResourceGroupApplicationRegistration
{
    public static IServiceCollection AddResourceGroupsFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddResourceGroupHttpMappings();
        services.AddSingleton<SearchSourceType>(ResourceGroupSearchProjection.SourceType);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<KubernetesOptions>,
                RuntimeCellNamespaceOptionsValidator>());
        services.AddOptions<KubernetesOptions>()
            .Bind(configuration.GetSection(KubernetesOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IOptions<KubernetesOptions>>()
            .Value);

        services.AddResourceGroupPersistence();
        services.AddCapability<RuntimeCellNamespaceNameGenerator,
            GenerateRuntimeCellNamespaceName>(implementation => implementation.Invoke);
        services.TryAddScoped<KubernetesRuntimeCellNamespaceManager>();
        services.TryAddScoped<EnsureRuntimeCellNamespace>(serviceProvider => serviceProvider
            .GetRequiredService<KubernetesRuntimeCellNamespaceManager>()
            .EnsureCreatedAsync);
        services.TryAddScoped<DeleteRuntimeCellNamespace>(serviceProvider => serviceProvider
            .GetRequiredService<KubernetesRuntimeCellNamespaceManager>()
            .DeleteAsync);
        services.TryAddScoped<RuntimeCellLifecyclePersistence>();
        services.TryAddScoped<RuntimeCellRecoveryPersistence>();
        services.TryAddScoped<RuntimeCellRecoveryJob>();
        return services;
    }

    public static IServiceProvider ScheduleResourceGroupRecurringJobs(
        this IServiceProvider services)
    {
        RecurringJob.AddOrUpdate<RuntimeCellRecoveryJob>(
            "recover-runtime-cells",
            job => job.RecoverAsync(CancellationToken.None),
            Cron.Minutely);
        return services;
    }

    private static void AddResourceGroupPersistence(this IServiceCollection services)
    {
        services.TryAddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IMongoDatabase>()
            .GetCollection<ResourceGroup>("ResourceGroups"));
        services.TryAddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IMongoDatabase>()
            .GetCollection<IsolationScope>("IsolationScopes"));
        services.TryAddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IMongoDatabase>()
            .GetCollection<RuntimeCell>("RuntimeCells"));

        services.AddCapability<MongoGetResourceGroupOwnership,
            GetResourceGroupOwnership>(implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadResourceGroup, LoadResourceGroup>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadResourceGroupPage, LoadResourceGroupPage>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadIsolationScope, LoadIsolationScope>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadIsolationScopes, LoadIsolationScopes>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadRuntimeCell, LoadRuntimeCell>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadRuntimeCells, LoadRuntimeCells>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoPersistCreatedResourceGroup, PersistCreatedResourceGroup>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoPersistUpdatedResourceGroup, PersistUpdatedResourceGroup>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoPersistCreatedIsolationScope, PersistCreatedIsolationScope>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoPersistRuntimeCellRetry, PersistRuntimeCellRetry>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoRegisterResourceGroupWebService,
            RegisterResourceGroupWebService>(implementation => implementation.InvokeAsync);
        services.AddCapability<MongoBeginResourceGroupDeletion, BeginResourceGroupDeletion>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoBeginIsolationScopeDeletion, BeginIsolationScopeDeletion>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoFinalizeResourceGroupDeletion,
            FinalizeResourceGroupDeletion>(implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadResourcePlacement, LoadResourcePlacement>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadResourcePlacementInTransaction,
            LoadResourcePlacementInTransaction>(implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadResourcePlacementsInTransaction,
            LoadResourcePlacementsInTransaction>(implementation => implementation.InvokeAsync);

        services.TryAddScoped<MongoResourcePlacementResolver>();
        services.TryAddScoped<ResolveResourceGroupPlacement>(serviceProvider => serviceProvider
            .GetRequiredService<MongoResourcePlacementResolver>()
            .InvokeAsync);
        services.TryAddScoped<ResolveResourceGroupPlacements>(serviceProvider => serviceProvider
            .GetRequiredService<MongoResourcePlacementResolver>()
            .InvokeManyAsync);

        services.TryAddSingleton<RunMongoOutcomeTransaction<ResourceGroup>>(
            serviceProvider => serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<ResourceGroup>);
        services.TryAddSingleton<RunMongoOutcomeTransaction<Result<
            ResourceGroup,
            ResourceGroupRevisionConflict>>>(serviceProvider => serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<Result<ResourceGroup, ResourceGroupRevisionConflict>>);
        services.TryAddSingleton<RunMongoOutcomeTransaction<Result<
            IsolationScope,
            PersistCreatedIsolationScopeError>>>(serviceProvider => serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<Result<IsolationScope, PersistCreatedIsolationScopeError>>);
        services.TryAddSingleton<RunMongoOutcomeTransaction<RetryRuntimeCellResult>>(
            serviceProvider => serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<RetryRuntimeCellResult>);
        services.TryAddSingleton<RunMongoOutcomeTransaction<BeginResourceGroupDeletionOutcome>>(
            serviceProvider => serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<BeginResourceGroupDeletionOutcome>);
        services.TryAddSingleton<RunMongoOutcomeTransaction<BeginIsolationScopeDeletionOutcome>>(
            serviceProvider => serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<BeginIsolationScopeDeletionOutcome>);
        services.TryAddSingleton<RunMongoOutcomeTransaction<Unit>>(serviceProvider => serviceProvider
            .GetRequiredService<MongoRunTransaction>()
            .InvokeOutcomeAsync<Unit>);
        services.TryAddSingleton<RunMongoOutcomeTransaction<bool>>(serviceProvider => serviceProvider
            .GetRequiredService<MongoRunTransaction>()
            .InvokeOutcomeAsync<bool>);
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
