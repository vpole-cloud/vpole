using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Application.Endpoints.Sources.Previews;
using Pole.Features.GitOps.Application.Inventory;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Application.Mongo;
using Pole.Features.GitOps.Application.Planning;
using Pole.Features.GitOps.Application.Runs;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application;

public static class GitOpsFeature
{
    public static IServiceCollection AddGitOpsFeature(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.AllowOutOfOrderMetadataProperties = true);
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>()
            .GetCollection<ManagedDeclaration>(GitOpsCollections.Inventory));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>()
            .GetCollection<GitOperationReceipt>(GitOpsCollections.Receipts));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>()
            .GetCollection<RepositorySource>(RepositoryCollections.Sources));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>()
            .GetCollection<GitSynchronizationState>(RepositoryCollections.Synchronization));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>()
            .GetCollection<GitReconciliationRun>(RepositoryCollections.Runs));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>()
            .GetCollection<GitSourceRequestReceipt>(RepositoryCollections.Requests));
        services.AddOptions<GitOpsWorkerOptions>()
            .BindConfiguration(GitOpsWorkerOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(
                options => options.HeartbeatSeconds * 2 < options.LeaseSeconds,
                "The heartbeat interval must be less than half the lease duration.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<GitOpsWorkerOptions>>().Value);
        services.AddScoped<RepositoryProviderRegistry>();
        services.AddScoped<GitRepositorySources>();
        services.AddScoped<GitSourceLifecycleStore>();
        services.AddScoped<GitSourceScheduling>();
        services.AddScoped<IGitSourceScheduler>(sp => sp.GetRequiredService<GitSourceScheduling>());
        services.AddScoped<GitSourceRecoveryScanner>();
        services.AddScoped<DispatchGitSource>(sp => source =>
            sp.GetRequiredService<IBackgroundJobClient>()
                .Enqueue<RepositorySourceJob>(job => job.ExecuteAsync(
                    source.TenantId,
                    source.SourceId,
                    CancellationToken.None)));
        services.AddScoped<ScheduleGitSourceAt>(sp => (source, notBeforeUtc) =>
            sp.GetRequiredService<IBackgroundJobClient>()
                .Schedule<RepositorySourceJob>(
                    job => job.ExecuteAsync(source.TenantId, source.SourceId, CancellationToken.None),
                    HangfireEnqueueAt(notBeforeUtc)));
        services.AddScoped<GitRunStore>();
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>()
            .GetCollection<GitOperationPreview>(RepositoryCollections.Previews));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>()
            .GetCollection<GitLifecycleCleanupProof>(GitOpsCollections.CleanupProofs));
        services.AddScoped<GitLifecycleCleanupProofs>();
        services.AddScoped<IGitLifecycleCleanupProofRecorder>(sp => sp.GetRequiredService<GitLifecycleCleanupProofs>());
        services.AddScoped<GitLifecycleResources>();
        services.AddScoped<GitLifecyclePreviewReferences>();
        services.AddScoped<GitLifecycleSnapshotReader>();
        services.AddScoped<GitLifecyclePreviewBuilder>();
        services.AddScoped<GitPrunePlanning>();
        services.AddScoped<GitLifecyclePreviews>();
        services.AddScoped<GitLifecycleExecutor>();
        services.AddScoped<GitAcceptedSnapshotExecutor>();
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>()
            .GetCollection<GitPlannedOperation>(GitOpsCollections.Plans));
        services.AddScoped<GitOperationPlanning>();
        services.AddScoped<GitOperationReportReader>();
        services.AddScoped<GitSnapshotAcceptance>();
        services.AddScoped<GitAcceptedReferences>();
        services.AddScoped<RepositorySourceJob>();
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>()
            .GetCollection<GitAcceptedReference>(RepositoryCollections.References));
        services.AddScoped<GitRepositoryQueries>();
        services.AddScoped<GitRepositoryInventory>();
        services.AddScoped<GitAdoptionCandidates>();
        services.AddScoped<DeclarativeProviderRegistry>();
        services.AddScoped<ManifestCompiler>();
        services.AddScoped<DeclarativeResourcePlanner>();
        services.AddScoped<MongoGitOwnership>();
        services.AddScoped<MongoGitLifecycleGuard>();
        services.AddScoped<IGitLifecyclePlanGuard>(sp => sp.GetRequiredService<MongoGitLifecycleGuard>());
        services.AddScoped<GitDeclarationCompletion>();
        services.AddScoped<GitOperationReceipts>();
        services.AddScoped<GitOwnershipStatuses>();
        services.AddScoped<IGitOpsMutations, MongoGitOpsMutations>();

        return services;
    }

    private static DateTimeOffset HangfireEnqueueAt(DateTime due)
    {
        // Hangfire stores schedule scores in whole seconds; truncation must not precede the durable due fence.
        var fractionalTicks = due.Ticks % TimeSpan.TicksPerSecond;
        return new DateTimeOffset(fractionalTicks == 0 ? due : due.AddTicks(TimeSpan.TicksPerSecond - fractionalTicks));
    }

    public static IEndpointRouteBuilder MapGitOpsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/gitops/sources")
            .WithTags("GitOpsSources")
            .MapCreateRepositorySourceEndpoint()
            .MapGetRepositorySourceEndpoint()
            .MapListRepositorySourcesEndpoint()
            .MapUpdateRepositorySourceEndpoint()
            .MapSyncRepositorySourceEndpoint()
            .MapRetryRepositorySourceEndpoint()
            .MapPauseRepositorySourceEndpoint()
            .MapResumeRepositorySourceEndpoint()
            .MapDeleteRepositorySourceEndpoint()
            .MapCreateLifecyclePreviewEndpoint()
            .MapConfirmLifecyclePreviewEndpoint()
            .MapGetLifecyclePreviewEndpoint()
            .MapListLifecyclePreviewEndpoint()
            .MapListRepositoryRunsEndpoint()
            .MapGetRepositoryRunEndpoint()
            .MapListRepositoryOperationsEndpoint()
            .MapListRepositoryResourcesEndpoint()
            .MapListAdoptionCandidatesEndpoint();

        return app;
    }

    public static IServiceProvider ScheduleGitOpsRecurringJobs(this IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<GitOpsWorkerOptions>>().Value;
        RecurringJob.AddOrUpdate<GitSourceRecoveryScanner>(
            "gitops-source-recovery",
            job => job.ScanAsync(CancellationToken.None),
            $"*/{options.ScanIntervalMinutes} * * * *");
        return services;
    }
}
