using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Declarations;
using Pole.Features.Backups.Application.Destinations;
using Pole.Features.Backups.Application.Endpoints;
using Pole.Features.Backups.Application.Jobs;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Application.Operations;
using Pole.Features.Backups.Application.Policies;
using Pole.Features.Backups.Application.Providers;
using Pole.Features.Backups.Application.Reconciliation;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;

namespace Pole.Features.Backups.Application;

public static class BackupsFeature
{
    public static IServiceCollection AddBackupsFeature(this IServiceCollection services)
    {
        services.AddBackupsMongo();
        services.AddBackupDeclarations();
        services.AddScoped<IIsolationScopeReferenceGuard, BackupDestinationScopeGuard>();
        services.AddScoped<BackupProviderCatalog>();
        services.AddScoped<BackupDestinationWrites>();
        services.AddScoped<BeginBackupPolicyDeletion>();
        services.AddScoped<DeleteBackupDestination>();
        services.AddScoped<CreateBackupArchive>();
        services.AddScoped<RetireBackupArchive>();
        services.AddScoped<BackupPolicyWrites>();
        services.AddScoped<BackupArchiveAccess>();
        services.AddScoped<RequestManualBackup>();
        services.AddScoped<RequestBackupRestore>();
        services.AddScoped<VerifyBackupRestoreTarget>();
        services.AddScoped<ScheduledBackupHistory>();
        services.AddScoped<LoadArchiveBackupCatalog>();
        services.AddScoped<RestoreTargetCreations>();
        services.AddScoped<RecordRestoreTargetCreation>(sp =>
            sp.GetRequiredService<RestoreTargetCreations>().RecordAsync);
        services.AddScoped<BackupDestinationQueries>();
        services.AddScoped<BackupPolicyQueries>();
        services.AddScoped<BackupArchiveQueries>();
        services.AddScoped<LoadBackupOperationHistory>();
        services.AddScoped<LoadBackupOperation>();
        services.AddScoped<ApplyBackupPolicy>();
        services.AddScoped<ExecuteBackupOperation>();
        services.AddScoped<ExecuteBackupCopy>();
        services.AddScoped<ExecuteBackupRestore>();
        services.AddScoped<BackupRecoveryScanner>();
        services.AddScoped<ObserveBackupArchiveHistory>();
        services.AddScoped<BackupArchiveHistoryJob>();
        services.AddScoped<BackupPolicyJob>();
        services.AddScoped<BackupOperationJob>();
        services.AddScoped<BackupRecoveryJob>();
        services.AddScoped<BackupSourceLifecycle>();
        services.AddScoped<ForgetRetiredArchive>();
        services.AddScoped<BackupAccessPlacementAdapter>();
        services.AddSingleton(
            ResourceDeploymentAdapterDescriptor.Create<BackupAccessPlacementAdapter>(
                BackupResourceTypes.Destination,
                ResourcePlacementCapability.MultiTarget));
        return services;
    }

    public static void ValidateBackupProviders(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        _ = scope.ServiceProvider.GetRequiredService<BackupProviderCatalog>();
    }

    public static IServiceProvider ScheduleBackupRecurringJobs(this IServiceProvider services)
    {
        RecurringJob.AddOrUpdate<BackupRecoveryJob>(
            "recover-backup-work",
            job => job.RecoverAsync(CancellationToken.None),
            Cron.Minutely);
        return services;
    }

    public static IEndpointRouteBuilder MapBackupsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup("/backups").MapBackupRoutes();
        return endpoints;
    }
}

/// <summary>The placement owns the retained namespace reference for S3 credential projections.
/// Secrets reconciles those projections. The destination runs no pods of its own.</summary>
internal sealed class BackupAccessPlacementAdapter : IProjectionOnlyPlacementAdapter
{
    public Task<ResourceDeploymentAdapterResult> ApplyAsync(
        ResourceDeploymentReconciliationContext context,
        CancellationToken ct) =>
        Task.FromResult<ResourceDeploymentAdapterResult>(ResourceDeploymentAdapterResult.Succeeded.Create());

    public Task<ResourceDeploymentAdapterResult> DeleteAsync(
        ResourceDeploymentReconciliationContext context,
        CancellationToken ct) =>
        Task.FromResult<ResourceDeploymentAdapterResult>(ResourceDeploymentAdapterResult.Succeeded.Create());
}
