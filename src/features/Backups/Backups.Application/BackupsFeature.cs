using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Declarations;
using Pole.Features.Backups.Application.Endpoints;
using Pole.Features.Backups.Application.Execution;
using Pole.Features.Backups.Application.Jobs;
using Pole.Features.Backups.Application.Operations;
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
        AddCollection<BackupDestination>(services, "BackupDestinations");
        AddCollection<BackupPolicy>(services, "BackupPolicies");
        AddCollection<BackupArchive>(services, "BackupArchives");
        AddCollection<BackupOperation>(services, "BackupOperations");
        services.AddBackupDeclarations();
        services.AddScoped<IIsolationScopeReferenceGuard, BackupDestinationScopeGuard>();
        services.AddScoped<BackupProviders>();
        services.AddScoped<BackupConfigurationWriter>();
        services.AddScoped<BackupConfigurationDeletions>();
        services.AddScoped<ManualBackupConfiguration>();
        services.AddScoped<BackupArchiveFactory>();
        services.AddScoped<BackupArchiveAccess>();
        services.AddScoped<BackupOperations>();
        services.AddScoped<RestoreTargetCreations>();
        services.AddScoped<RecordRestoreTargetCreation>(sp =>
            sp.GetRequiredService<RestoreTargetCreations>().RecordAsync);
        services.AddScoped<BackupReads>();
        services.AddScoped<LoadBackupOperationHistory>();
        services.AddScoped<ApplyBackupPolicy>();
        services.AddScoped<ExecuteBackupOperation>();
        services.AddScoped<BackupRecoveryScanner>();
        services.AddScoped<BackupPolicyJob>();
        services.AddScoped<BackupOperationJob>();
        services.AddScoped<BackupRecoveryJob>();
        services.AddScoped<BackupSourceLifecycle>();
        services.AddScoped<ForgetRetiredArchive>();
        services.AddScoped<BackupAccessPlacementAdapter>();
        services.AddSingleton(
            ResourceDeploymentAdapterDescriptor.Create<BackupAccessPlacementAdapter>(
                BackupConfigurationWriter.DestinationType,
                ResourcePlacementCapability.MultiTarget));
        return services;
    }

    public static void ValidateBackupProviders(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        _ = scope.ServiceProvider.GetRequiredService<BackupProviders>();
    }

    public static IServiceProvider ScheduleBackupRecurringJobs(this IServiceProvider services)
    {
        RecurringJob.AddOrUpdate<BackupRecoveryJob>(
            "recover-backup-work",
            job => job.RecoverAsync(CancellationToken.None),
            Cron.Minutely);
        return services;
    }

    private static void AddCollection<T>(IServiceCollection services, string name)
    {
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>().GetCollection<T>(name));
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
