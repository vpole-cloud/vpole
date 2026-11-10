using System.Reflection;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pole.Features.Outbox.Application.Delivery;

namespace Pole.Features.Outbox.Application;

public static class OutboxFeatureRegistration
{
    internal const string ProcessingJobId = "process-outbox-messages";

    public static IServiceCollection AddOutboxFeature(
        this IServiceCollection services,
        IConfiguration configuration,
        params Assembly[] eventAssemblies)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(eventAssemblies);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<OutboxSettings>, OutboxSettingsValidator>());
        services.AddOptions<OutboxSettings>()
            .Bind(configuration.GetSection(OutboxSettings.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IOptions<OutboxSettings>>()
            .Value);
        services.TryAddSingleton(new OutboxEventAssemblyCatalog(eventAssemblies));
        services.AddOutboxPersistence();
        services.TryAddSingleton<EnsurePendingOutboxEventsRegistered>();
        services.TryAddScoped<OutboxProcessorJob>();
        services.TryAddScoped<IOutboxPublisher, MassTransitOutboxPublisher>();
        services.TryAddSingleton<OutboxMetrics>();
        services.TryAddSingleton<OutboxRetryDelayPolicy>();
        services.TryAddSingleton(TimeProvider.System);
        // Slice hosts may disable the optional loop and omit Hangfire storage.
        // Resolve its dependencies only when an enabled loop asks for dispatch.
        services.TryAddSingleton<IOutboxProcessingTrigger>(provider => new DirectOutboxProcessingTrigger(
            provider.GetRequiredService<JobStorage>(),
            provider.GetRequiredService<IServiceScopeFactory>()));
        services.AddHostedService<OutboxProcessingTriggerService>();
        return services;
    }

    public static IServiceProvider ScheduleOutboxRecurringJobs(this IServiceProvider services)
    {
        var settings = services.GetRequiredService<OutboxSettings>();
        RecurringJob.AddOrUpdate<OutboxProcessorJob>(
            ProcessingJobId,
            job => job.ProcessOutboxMessagesAsync(CancellationToken.None),
            settings.ProcessingCronExpression);
        RecurringJob.AddOrUpdate<OutboxProcessorJob>(
            "cleanup-outbox-messages",
            job => job.CleanupMessagesAsync(CancellationToken.None),
            Cron.Daily(3));
        return services;
    }
}
