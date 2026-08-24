using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pole.Features.Outbox.Application.Persistence;

namespace Pole.Features.Outbox.Application;

public static class OutboxFeatureRegistration
{
    public static IServiceCollection AddOutboxFeature(
        this IServiceCollection services,
        IConfiguration configuration,
        params Assembly[] eventAssemblies)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(eventAssemblies);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<OutboxSettings>,
                OutboxSettingsValidator>());
        services.AddOptions<OutboxSettings>()
            .Bind(configuration.GetSection(OutboxSettings.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IOptions<OutboxSettings>>()
            .Value);
        services.TryAddSingleton(new OutboxEventAssemblyCatalog(eventAssemblies));
        services.AddOutboxPersistence();
        services.TryAddScoped<OutboxProcessorJob>();
        services.TryAddScoped<IOutboxPublisher, MassTransitOutboxPublisher>();
        services.TryAddSingleton<OutboxMetrics>();
        services.TryAddSingleton<OutboxRetryDelayPolicy>();
        return services;
    }
}
