using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Realtime.Contracts;
using Pole.Features.ResourceConnections.Application.Endpoints;
using Pole.Features.ResourceConnections.Application.Jobs;
using Pole.Features.ResourceConnections.Application.Kubernetes;
using Pole.Features.ResourceConnections.Application.Persistence;
using Pole.Features.ResourceConnections.Application.Reconciliation;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceConnections.Contracts;

namespace Pole.Features.ResourceConnections.Application;

public static class ResourceConnectionsFeature
{
    public static IServiceCollection AddResourceConnectionsFeature(
        this IServiceCollection services)
    {
        services.AddSingleton(new RealtimeTopic("resource-connections", RealtimeScope.Tenant));
        services.TryAddSingleton(provider => provider
            .GetRequiredService<IMongoDatabase>()
            .GetCollection<ResourceConnection>("ResourceConnections"));
        services.TryAddSingleton(provider => provider
            .GetRequiredService<IMongoDatabase>()
            .GetCollection<ResourceConnectionCleanupTombstone>(
                "ResourceConnectionCleanupTombstones"));
        services.TryAddScoped<MongoCreateResourceConnection>();
        services.TryAddScoped<ResourceConnectionEndpointCatalog>();
        services.AddScoped<ResourceConnectionReferenceProvider>();
        services.AddScoped<AdmitResourceConnectionReferences>(provider => provider.GetRequiredService<ResourceConnectionReferenceProvider>().AdmitAsync);
        services.AddScoped<ResolveResourceConnectionReferences>(provider => provider.GetRequiredService<ResourceConnectionReferenceProvider>().ResolveAsync);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService,
            ResourceConnectionCapabilityStartupValidator>());
        services.TryAddScoped<MongoResourceConnectionMutationProvider>();
        services.TryAddScoped<ReconcileConnectionsForResource>(provider => provider
            .GetRequiredService<MongoResourceConnectionMutationProvider>()
            .ReconcileAsync);
        services.TryAddScoped<CascadeConnectionsForResourceDeletion>(provider => provider
            .GetRequiredService<MongoResourceConnectionMutationProvider>()
            .CascadeAsync);
        services.TryAddScoped<RunMongoOutcomeTransaction<
            CreateResourceConnectionPersistenceOutcome>>(provider => provider
            .GetRequiredService<MongoRunTransaction>()
            .InvokeOutcomeAsync<CreateResourceConnectionPersistenceOutcome>);
        services.TryAddScoped<PersistResourceConnectionCreation>(provider => provider
            .GetRequiredService<MongoCreateResourceConnection>()
            .InvokeAsync);
        services.TryAddScoped<MongoResourceConnectionReads>();
        services.TryAddScoped<LoadResourceConnection>(provider => provider
            .GetRequiredService<MongoResourceConnectionReads>()
            .LoadOneAsync);
        services.TryAddScoped<LoadResourceConnectionsPage>(provider => provider
            .GetRequiredService<MongoResourceConnectionReads>()
            .LoadPageAsync);
        services.TryAddScoped<MongoBeginResourceConnectionDeletion>();
        services.TryAddScoped<RunMongoOutcomeTransaction<
            DeleteResourceConnectionPersistenceOutcome>>(provider => provider
            .GetRequiredService<MongoRunTransaction>()
            .InvokeOutcomeAsync<DeleteResourceConnectionPersistenceOutcome>);
        services.TryAddScoped<BeginResourceConnectionDeletion>(provider => provider
            .GetRequiredService<MongoBeginResourceConnectionDeletion>()
            .InvokeAsync);
        services.TryAddScoped<MongoRecordResourceConnectionTarget>();
        services.TryAddScoped<MongoResourceConnectionTombstonePersistence>();
        services.TryAddScoped<RunMongoOutcomeTransaction<bool>>(provider => provider
            .GetRequiredService<MongoRunTransaction>()
            .InvokeOutcomeAsync<bool>);
        services.TryAddScoped<LoadResourceConnectionTombstone>(provider => provider
            .GetRequiredService<MongoResourceConnectionTombstonePersistence>()
            .LoadAsync);
        services.TryAddScoped<LoadResourceConnectionTombstoneRecoveryPage>(provider => provider
            .GetRequiredService<MongoResourceConnectionTombstonePersistence>()
            .LoadPageAsync);
        services.TryAddScoped<RecordResourceConnectionApply>(provider => provider
            .GetRequiredService<MongoRecordResourceConnectionTarget>()
            .RecordApplyAsync);
        services.TryAddScoped<RecordResourceConnectionCleanup>(provider => provider
            .GetRequiredService<MongoRecordResourceConnectionTarget>()
            .RecordCleanupAsync);
        services.TryAddScoped<RecordResourceConnectionReverificationFailure>(provider => provider
            .GetRequiredService<MongoRecordResourceConnectionTarget>()
            .RecordReverificationFailureAsync);
        services.TryAddScoped<RecordResourceConnectionCleanupVerificationFailure>(provider => provider
            .GetRequiredService<MongoRecordResourceConnectionTarget>()
            .RecordCleanupVerificationFailureAsync);
        services.TryAddScoped<PurgeResourceConnectionIfComplete>(provider => provider
            .GetRequiredService<MongoResourceConnectionTombstonePersistence>()
            .PurgeIfCompleteAsync);
        services.TryAddScoped<IResourceConnectionPolicyTarget,
            KubernetesResourceConnectionPolicyTarget>();
        services.TryAddScoped<ResourceConnectionReconciler>();
        services.TryAddScoped<MongoLoadResourceConnectionRecoveryPage>();
        services.TryAddScoped<LoadResourceConnectionRecoveryPage>(provider => provider
            .GetRequiredService<MongoLoadResourceConnectionRecoveryPage>()
            .InvokeAsync);
        services.TryAddScoped<ResourceConnectionRecoveryJob>();
        return services;
    }

    public static IServiceProvider ScheduleResourceConnectionRecurringJobs(
        this IServiceProvider services)
    {
        RecurringJob.AddOrUpdate<ResourceConnectionRecoveryJob>(
            "recover-resource-connections",
            job => job.RecoverAsync(CancellationToken.None),
            Cron.Minutely);
        return services;
    }

    public static IEndpointRouteBuilder MapResourceConnectionEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapGroup("/resource-connections")
            .WithTags("ResourceConnections")
            .MapCreateResourceConnectionEndpoint()
            .MapGetResourceConnectionEndpointsEndpoint()
            .MapGetResourceConnectionEndpoint()
            .MapGetResourceConnectionsEndpoint()
            .MapDeleteResourceConnectionEndpoint();
        return app;
    }
}
