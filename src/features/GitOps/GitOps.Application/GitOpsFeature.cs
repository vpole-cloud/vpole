using Pole.Features.GitOps.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Hangfire;
using Microsoft.AspNetCore.Routing;
using Pole.Features.GitOps.Application.Endpoints;
using Pole.Features.GitOps.Application.Jobs;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Features.GitOps.Contracts;

namespace Pole.Features.GitOps.Application;

public static class GitOpsFeature
{
    public static IServiceCollection AddGitOpsFeature(this IServiceCollection services)
    {
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>()
                .GetCollection<GitSourceExecution>(GitOpsCollections.Executions));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>()
                .GetCollection<ManagedDeclaration>(GitOpsCollections.Inventory));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>()
                .GetCollection<GitOperationReceipt>(GitOpsCollections.Receipts));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>()
                .GetCollection<ManualSnapshotSource>(GitOpsCollections.Sources));
        services.AddScoped<ManualSnapshotSources>();
        services.AddScoped<SnapshotResumeRecoveryJob>();
        services.AddScoped<ResumeSnapshotOnSecretValueChanged>();
        services.AddScoped<DeclarativeProviderRegistry>();
        services.AddScoped<ManifestCompiler>();
        services.AddScoped<DeclarativeResourcePlanner>();
        services.AddScoped<MongoGitOwnership>();
        services.AddScoped<GitDeclarationCompletion>();
        services.AddScoped<GitOperationReceipts>();
        services.AddScoped<GitOwnershipStatuses>();
        services.AddScoped<SnapshotAcceptanceStore>();
        services.AddScoped<AcceptSnapshot>(sp => sp.GetRequiredService<SnapshotAcceptanceStore>().InvokeAsync);
        services.AddScoped<SnapshotAttemptStore>();
        services.AddScoped<ClaimSnapshotAttempt>(sp => sp.GetRequiredService<SnapshotAttemptStore>().ClaimAsync);
        services.AddScoped<RejectStoredSnapshotSchema>(sp => sp.GetRequiredService<SnapshotAttemptStore>()
                .RejectSchemaAsync);
        services.AddScoped<FinalizeSnapshotAttempt>(sp => sp.GetRequiredService<SnapshotAttemptStore>().FinalizeAsync);
        services.AddScoped<IGitOpsMutations, MongoGitOpsMutations>();
        services.AddScoped<DeclarativePipeline>();

        return services;
    }

    public static IEndpointRouteBuilder MapGitSnapshotEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/gitops/sources").WithTags("ManualGitSnapshots")
            .MapCreateSnapshotSourceEndpoint().MapGetSnapshotSourceEndpoint()
            .MapSubmitSnapshotEndpoint().MapResumeSnapshotEndpoint();

        return app;
    }

    public static IServiceProvider ScheduleGitSnapshotRecurringJobs(this IServiceProvider services)
    {
        RecurringJob.AddOrUpdate<SnapshotResumeRecoveryJob>(
            "resume-manual-git-snapshots",
            job => job.RecoverAsync(CancellationToken.None),
            Cron.Minutely);

        return services;
    }
}
