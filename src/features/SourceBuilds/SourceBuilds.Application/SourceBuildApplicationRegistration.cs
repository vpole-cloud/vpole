using Microsoft.Extensions.Configuration;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pole.Features.Realtime.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.SourceBuilds.Application.Execution;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Jobs;
using Pole.Features.SourceBuilds.Application.Persistence;
using Pole.Features.SourceBuilds.Application.Persistence.Configuration;
using Pole.Features.SourceBuilds.Application.Persistence.Jobs;
using Pole.Features.SourceBuilds.Application.Persistence.Logs;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application;

public static class SourceBuildApplicationRegistration
{
    public static IServiceCollection AddSourceBuildsFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddScoped<DeclarativeSourceBuildConfigurationProvider>();
        services.AddScoped<IDeclarativeResourceProvider>(provider =>
            provider.GetRequiredService<DeclarativeSourceBuildConfigurationProvider>());
        services.AddScoped<IDeclarativeResourceLifecycleProvider>(provider =>
            provider.GetRequiredService<DeclarativeSourceBuildConfigurationProvider>());
        services.AddScoped<IDeclarativeLifecycleDependencyProvider>(provider =>
            provider.GetRequiredService<DeclarativeSourceBuildConfigurationProvider>());
        services.AddSingleton(new RealtimeTopic("source-builds", RealtimeScope.Tenant));
        services.Configure<SourceBuildExecutionOptions>(
            configuration.GetSection(SourceBuildExecutionOptions.SectionName));
        services.TryAddSingleton(provider =>
            provider.GetRequiredService<IOptions<SourceBuildExecutionOptions>>().Value);
        services.Configure<BuildKitOptions>(configuration.GetSection(BuildKitOptions.SectionName));
        services.TryAddSingleton(provider =>
            provider.GetRequiredService<IOptions<BuildKitOptions>>().Value);
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<SourceBuildLogOptions>().Bind(configuration.GetSection(SourceBuildLogOptions.SectionName))
            .Validate(options => options.Valid, "SourceBuildLogs limits are outside the supported bounds.")
            .ValidateOnStart();
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<SourceBuildLogOptions>>().Value);
        services.TryAddSingleton(provider =>
            provider.GetRequiredService<IMongoDatabase>().GetCollection<SourceBuildLog>("SourceBuildLogs"));
        services.TryAddScoped<SourceBuildLogPersistence>();
        services.AddCapability<MongoStartSourceBuildLog, StartSourceBuildLog>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoAppendSourceBuildLog, AppendSourceBuildLog>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoRenewSourceBuildLog, RenewSourceBuildLog>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoSealSourceBuildLog, SealSourceBuildLog>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoLoadSourceBuildLog, LoadSourceBuildLog>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoRecoverSourceBuildLogs, RecoverSourceBuildLogs>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<SourceBuildLogCaptureFactory, CreateSourceBuildLogCapture>(implementation =>
            implementation.CreateAsync);

        services.TryAddSingleton(provider => provider
            .GetRequiredService<IMongoDatabase>()
            .GetCollection<SourceBuildConfiguration>("SourceBuildConfigurations"));
        services.TryAddSingleton(provider => provider
            .GetRequiredService<IMongoDatabase>()
            .GetCollection<SourceBuildJob>("SourceBuildJobs"));

        services.TryAddSingleton(provider => provider.GetRequiredService<IMongoDatabase>()
            .GetCollection<SourceBuildPromotionReceipt>("SourceBuildPromotionReceipts"));
        services.AddCapability<SourceBuildPromotion, BeginSourceBuildPromotion>(implementation =>
            implementation.BeginAsync);
        services.AddCapability<SourceBuildPromotion, CompleteSourceBuildPromotion>(implementation =>
            implementation.CompleteAsync);

        services.AddCapability<SourceBuildConfigurationReferences, LoadSourceBuildConfigurationReference>(
            implementation => implementation.LoadAsync);
        services.AddCapability<SourceBuildConfigurationReferences, FindNamedSourceBuildConfiguration>(
            implementation => implementation.FindNamedAsync);
        services.AddCapability<SourceBuildConfigurationReferences, FenceSourceBuildConfiguration>(
            implementation => implementation.FenceAsync);
        services.AddCapability<MongoLoadSourceBuildConfiguration, LoadSourceBuildConfiguration>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoLoadSourceBuildConfigurations, LoadSourceBuildConfigurations>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoCreateSourceBuildConfiguration, CreateSourceBuildConfiguration>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoUpdateSourceBuildConfiguration, UpdateSourceBuildConfiguration>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoDeleteSourceBuildConfiguration, DeleteSourceBuildConfiguration>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoLoadSourceBuildJob, LoadSourceBuildJob>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoLoadSourceBuildJobPage, LoadSourceBuildJobPage>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoQueueSourceBuildJob, QueueSourceBuildJob>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoRenewSourceBuildExecution, RenewSourceBuildExecution>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoClaimQueuedSourceBuildJob, ClaimQueuedSourceBuildJob>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoTryPersistSourceBuildJob, TryPersistSourceBuildJob>(implementation =>
            implementation.InvokeAsync);
        services.AddCapability<MongoLoadStaleQueuedSourceBuildJobs, LoadStaleQueuedSourceBuildJobs>(implementation =>
            implementation.InvokeAsync);
        services
            .AddCapability<MongoCompleteSourceBuildJobOnDeployment,
                CompleteSourceBuildJobOnDeployment>(implementation => implementation.InvokeAsync);

        services
            .AddCapability<MongoCleanupSourceBuildConfigurations, CleanupSourceBuildConfigurations>(implementation =>
                implementation.InvokeAsync);

        services.TryAddScoped<MongoEnsureNoWebServiceActiveSourceBuilds>();
        services.TryAddScoped<EnsureNoWebServiceActiveSourceBuilds>(provider =>
            provider.GetRequiredService<MongoEnsureNoWebServiceActiveSourceBuilds>().InvokeAsync);
        services.TryAddScoped<MongoEnsureNoSourceRepositoryBuildConfiguration>();
        services.TryAddScoped<EnsureNoSourceRepositoryBuildConfiguration>(provider =>
            provider.GetRequiredService<MongoEnsureNoSourceRepositoryBuildConfiguration>().InvokeAsync);

        if (!services.Any(descriptor => descriptor.ServiceType == typeof(DispatchSourceBuildJob)))
        {
            services.TryAddSingleton<HangfireSourceBuildJobDispatcher>();
            services.TryAddSingleton<DispatchSourceBuildJob>(provider =>
                provider.GetRequiredService<HangfireSourceBuildJobDispatcher>().Invoke);
        }

        services.TryAddSingleton<RepositoryArchiveExtractor>();
        services.TryAddSingleton<ExtractRepositoryArchive>(provider =>
            provider.GetRequiredService<RepositoryArchiveExtractor>().InvokeAsync);
        services.TryAddSingleton<ProcessRunner>();
        services.TryAddSingleton<RunProcess>(provider =>
            provider.GetRequiredService<ProcessRunner>().InvokeAsync);
        services.TryAddSingleton<BuildKitClient>();
        services.TryAddSingleton<BuildContainerImage>(provider =>
            provider.GetRequiredService<BuildKitClient>().InvokeAsync);
        services.TryAddScoped<SourceBuildWorkspaceService>();
        services.TryAddScoped<CreateSourceBuildWorkspace>(provider =>
            provider.GetRequiredService<SourceBuildWorkspaceService>().InvokeAsync);
        services.TryAddScoped<SourceBuildJobExecutor>();
        services.TryAddScoped<SourceBuildQueueRecoveryJob>();
        services.TryAddScoped<SourceBuildLogRecoveryJob>();
        return services;
    }

    public static IServiceProvider ScheduleSourceBuildRecurringJobs(
        this IServiceProvider services)
    {
        RecurringJob.AddOrUpdate<SourceBuildQueueRecoveryJob>(
            "recover-source-build-queue",
            job => job.RecoverAsync(),
            Cron.Minutely);
        RecurringJob.AddOrUpdate<SourceBuildLogRecoveryJob>("recover-source-build-logs",
            job => job.RecoverAsync(),
            Cron.Minutely);
        return services;
    }

    private static void AddCapability<TImplementation, TDelegate>(
        this IServiceCollection services,
        Func<TImplementation, TDelegate> bind)
        where TImplementation : class
        where TDelegate : Delegate
    {
        services.TryAddScoped<TImplementation>();
        services.TryAddScoped(provider =>
            bind(provider.GetRequiredService<TImplementation>()));
    }
}
