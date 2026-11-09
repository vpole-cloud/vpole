using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Realtime.Contracts;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.IsolationScopes;
using Pole.Features.ResourceGroups.Application.Jobs;
using Pole.Features.ResourceGroups.Application.Kubernetes;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Application.Placements;
using Pole.Features.ResourceGroups.Application.References;
using Pole.Features.ResourceGroups.Application.ResourceGroups;
using Pole.Features.ResourceGroups.Application.ResourceRegistrations;
using Pole.Features.ResourceGroups.Application.RuntimeCells;
using Pole.Features.ResourceGroups.Application.Search;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Search.Contracts;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application;

public static class ResourceGroupApplicationRegistration
{
    public static IResourceRegistrar CreatePluginResourceRegistrar(
        this IServiceProvider serviceProvider,
        string owningPluginId)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        var normalizedPluginId = ResourceRegistrationFactory.NormalizePluginId(owningPluginId);
        return new BoundResourceRegistrar(
            serviceProvider.GetRequiredService<MongoResourceRegistrationMutations>(),
            normalizedPluginId);
    }

    public static IServiceCollection AddResourceGroupsFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddResourceGroupHttpMappings();
        services.AddSingleton(new RealtimeTopic("resource-groups", RealtimeScope.Tenant));
        services.AddSingleton(new RealtimeTopic("workspace-inventory", RealtimeScope.Tenant));
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
        services.AddCapability<MongoResourceGroupPlanningSnapshots, LoadResourceGroupPlanningSnapshot>(
            implementation => implementation.ReadAsync);
        services.AddCapability<MongoResourceGroupPlanningSnapshots, FenceResourceGroupPlanningSnapshot>(
            implementation => implementation.FenceAsync);
        services.AddCapability<MongoAdmitResourceScopeReference, AdmitResourceScopeReference>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<RuntimeCellNamespaceNameGenerator,
            GenerateRuntimeCellNamespaceName>(implementation => implementation.Invoke);
        services.TryAddScoped<KubernetesRuntimeCellNamespaceManager>();
        services.TryAddScoped<KubernetesEnsureRuntimeCellIngressBaseline>();
        services.TryAddSingleton<EnsureRuntimeCellIngressBaseline>(serviceProvider =>
        {
            var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
            return async (request, cancellationToken) =>
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                return await scope.ServiceProvider
                    .GetRequiredService<KubernetesEnsureRuntimeCellIngressBaseline>()
                    .InvokeAsync(
                        request.TenantId,
                        request.RuntimeCellId,
                        request.DeploymentTargetId,
                        request.KubernetesNamespace,
                        cancellationToken);
            };
        });
        services.TryAddScoped<EnsureRuntimeCellNamespace>(serviceProvider => serviceProvider
            .GetRequiredService<KubernetesRuntimeCellNamespaceManager>()
            .EnsureCreatedAsync);
        services.TryAddScoped<DeleteRuntimeCellNamespace>(serviceProvider => serviceProvider
            .GetRequiredService<KubernetesRuntimeCellNamespaceManager>()
            .DeleteAsync);
        services.TryAddScoped<RuntimeCellDeletionProgress>();
        services.TryAddScoped<RuntimeCellDeletionJob>();
        services.TryAddScoped<ScheduleRuntimeCellDeletionAt>(provider => (request, due) =>
        {
            provider.GetRequiredService<IBackgroundJobClient>().Schedule<RuntimeCellDeletionJob>(job =>
                job.ExecuteAsync(request, CancellationToken.None), new DateTimeOffset(due));
            provider.GetService<ILogger<RuntimeCellDeletionJob>>()?.LogInformation(
                "RuntimeCell deletion check scheduled: TenantId={TenantId}, RuntimeCellId={RuntimeCellId}, Generation={Generation}, CheckSequence={CheckSequence}, NotBeforeUtc={NotBeforeUtc:O}",
                request.EventCore.TenantId, request.RuntimeCellId, request.Generation, request.CheckSequence, due);
        });
        services.TryAddScoped<RuntimeCellLifecyclePersistence>();
        services.TryAddScoped<RuntimeCellRecoveryPersistence>();
        services.TryAddScoped<RuntimeCellRecoveryJob>();
        services.AddScoped<ResourceGroupLifecycleReferences>();
        services.AddScoped<IDeclarativeLifecycleDependencyProvider>(provider =>
            provider.GetRequiredService<ResourceGroupLifecycleReferences>());
        services.AddScoped<DeclarativeResourceGroupProvider>();
        services.AddScoped<DeclarativeIsolationScopeProvider>();
        services.AddScoped<IDeclarativeResourceProvider>(provider =>
            provider.GetRequiredService<DeclarativeResourceGroupProvider>());
        services.AddScoped<IDeclarativeResourceProvider>(provider =>
            provider.GetRequiredService<DeclarativeIsolationScopeProvider>());
        services.AddScoped<IDeclarativeResourceLifecycleProvider>(provider =>
            provider.GetRequiredService<DeclarativeResourceGroupProvider>());
        services.AddScoped<IDeclarativeResourceLifecycleProvider>(provider =>
            provider.GetRequiredService<DeclarativeIsolationScopeProvider>());
        services.AddCapability<MongoIsolationScopePlanningSnapshots, ResolveNamedIsolationScope>(
            implementation => implementation.ResolveAsync);
        services.AddCapability<MongoIsolationScopePlanningSnapshots, LoadIsolationScopePlanningSnapshot>(
            implementation => implementation.ReadAsync);
        services.AddCapability<MongoIsolationScopePlanningSnapshots, FenceIsolationScopePlanningSnapshot>(
            implementation => implementation.FenceAsync);
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
        services.TryAddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IMongoDatabase>()
            .GetCollection<ResourceRegistration>("ResourceRegistrations"));

        services.AddCapability<MongoGetResourceGroupOwnership,
            GetResourceGroupOwnership>(implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadResourceGroup, LoadResourceGroup>(implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadResourceGroupPage, LoadResourceGroupPage>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoLoadIsolationScope, LoadIsolationScope>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoLoadIsolationScopes, LoadIsolationScopes>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoLoadRuntimeCell, LoadRuntimeCell>(implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadRuntimeCells, LoadRuntimeCells>(implementation => implementation.InvokeAsync);
        services.AddCapability<MongoPersistCreatedResourceGroup, PersistCreatedResourceGroup>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoPersistUpdatedResourceGroup, PersistUpdatedResourceGroup>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoPersistCreatedIsolationScope, PersistCreatedIsolationScope>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoPersistRuntimeCellRetry, PersistRuntimeCellRetry>(implementation =>
            implementation.InvokeAsync);
        services.TryAddScoped<MongoResourceRegistrationMutations>();
        services.TryAddScoped<MongoResourceRegistrationReads>();
        services.AddScoped<MongoResourceRegistrationPlanning>();
        services.AddScoped<LoadResourceRegistrationPlanningSnapshot>(provider =>
            provider.GetRequiredService<MongoResourceRegistrationPlanning>().LoadAsync);
        services.AddScoped<FenceResourceRegistrationPlanningSnapshot>(provider =>
            provider.GetRequiredService<MongoResourceRegistrationPlanning>().FenceAsync);
        services.TryAddScoped<LoadRegisteredResource>(provider =>
            provider.GetRequiredService<MongoResourceRegistrationReads>().LoadOneAsync);
        services.AddCapability<MongoResourceRegistrationConnectionFence,
            FenceRegisteredResource>(implementation => implementation.InvokeAsync);
        services.TryAddScoped<IResourceRegistrar>(serviceProvider =>
            new BoundResourceRegistrar(
                serviceProvider.GetRequiredService<MongoResourceRegistrationMutations>(),
                owningPluginId: null));
        services.TryAddScoped<LoadResourceRegistrationPage>(serviceProvider => serviceProvider
            .GetRequiredService<MongoResourceRegistrationReads>()
            .LoadPageAsync);
        services.TryAddScoped<LoadResourceRegistrationCount>(serviceProvider => serviceProvider
            .GetRequiredService<MongoResourceRegistrationReads>()
            .CountAsync);
        services.AddCapability<MongoBeginResourceGroupDeletion, BeginResourceGroupDeletion>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoBeginIsolationScopeDeletion, BeginIsolationScopeDeletion>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoFinalizeResourceGroupDeletion,
            FinalizeResourceGroupDeletion>(implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadResourcePlacement, LoadResourcePlacement>(implementation =>
            implementation.InvokeAsync);
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

        services.TryAddSingleton<RunMongoOutcomeTransaction<PersistCreatedResourceGroupResult>>(serviceProvider =>
            serviceProvider
            .GetRequiredService<MongoRunTransaction>()
            .InvokeOutcomeAsync<PersistCreatedResourceGroupResult>);
        services.TryAddSingleton<RunMongoOutcomeTransaction<PersistUpdatedResourceGroupOutcome>>(serviceProvider =>
            serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<PersistUpdatedResourceGroupOutcome>);
        services.TryAddSingleton<RunMongoOutcomeTransaction<PersistCreatedIsolationScopeOutcome>>(serviceProvider =>
            serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<PersistCreatedIsolationScopeOutcome>);
        services.TryAddSingleton<RunMongoOutcomeTransaction<RetryRuntimeCellResult>>(serviceProvider => serviceProvider
            .GetRequiredService<MongoRunTransaction>()
            .InvokeOutcomeAsync<RetryRuntimeCellResult>);
        services.TryAddSingleton<RunMongoOutcomeTransaction<BeginResourceGroupDeletionOutcome>>(serviceProvider =>
            serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<BeginResourceGroupDeletionOutcome>);
        services.TryAddSingleton<RunMongoOutcomeTransaction<BeginIsolationScopeDeletionOutcome>>(serviceProvider =>
            serviceProvider
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
