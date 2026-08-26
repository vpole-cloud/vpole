using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Execution;
using Pole.Features.SourceBuilds.Application.Jobs;
using Pole.Features.SourceBuilds.Application.Persistence;
using Pole.Features.SourceBuilds.Application.Persistence.Configuration;
using Pole.Features.SourceBuilds.Application.Persistence.Jobs;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application;

public static class SourceBuildApplicationRegistration
{
    public static IServiceCollection AddSourceBuildsFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<SourceBuildExecutionOptions>(
            configuration.GetSection(SourceBuildExecutionOptions.SectionName));
        services.TryAddSingleton(provider =>
            provider.GetRequiredService<IOptions<SourceBuildExecutionOptions>>().Value);
        services.Configure<BuildKitOptions>(configuration.GetSection(BuildKitOptions.SectionName));
        services.TryAddSingleton(provider =>
            provider.GetRequiredService<IOptions<BuildKitOptions>>().Value);
        services.AddHttpProblemMappings(SourceBuildHttpMappings.All);
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton(provider => provider
            .GetRequiredService<IMongoDatabase>()
            .GetCollection<SourceBuildConfiguration>("SourceBuildConfigurations"));
        services.TryAddSingleton(provider => provider
            .GetRequiredService<IMongoDatabase>()
            .GetCollection<SourceBuildJob>("SourceBuildJobs"));

        services.AddCapability<MongoLoadSourceBuildConfiguration, LoadSourceBuildConfiguration>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadSourceBuildConfigurations, LoadSourceBuildConfigurations>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoCreateSourceBuildConfiguration, CreateSourceBuildConfiguration>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoUpdateSourceBuildConfiguration, UpdateSourceBuildConfiguration>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoDeleteSourceBuildConfiguration, DeleteSourceBuildConfiguration>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadSourceBuildJob, LoadSourceBuildJob>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadSourceBuildJobForExecution, LoadSourceBuildJobForExecution>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadSourceBuildJobPage, LoadSourceBuildJobPage>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoQueueSourceBuildJob, QueueSourceBuildJob>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoClaimQueuedSourceBuildJob, ClaimQueuedSourceBuildJob>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoTryPersistSourceBuildJob, TryPersistSourceBuildJob>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoLoadStaleQueuedSourceBuildJobs, LoadStaleQueuedSourceBuildJobs>(
            implementation => implementation.InvokeAsync);
        services.AddCapability<MongoCompleteSourceBuildJobOnDeployment, CompleteSourceBuildJobOnDeployment>(
            implementation => implementation.InvokeAsync);

        services.TryAddScoped<MongoEnsureNoWebServiceSourceBuildConfiguration>();
        services.TryAddScoped<EnsureNoWebServiceSourceBuildConfiguration>(provider =>
            provider.GetRequiredService<MongoEnsureNoWebServiceSourceBuildConfiguration>().InvokeAsync);
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
