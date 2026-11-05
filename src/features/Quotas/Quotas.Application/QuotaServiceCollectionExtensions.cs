using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Pole.ASP.Helpers;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.Quotas.Application.Mongo;
using Pole.Features.Quotas.Application.Services;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Quotas.Contracts.HttpMapping;

namespace Pole.Features.Quotas.Application;

public static class QuotaServiceCollectionExtensions
{
    public static IServiceCollection AddQuotasFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddHttpProblemMappings(QuotaContractHttpMappings.All);
        services.AddOptions<QuotaSettings>()
            .Bind(configuration.GetSection(QuotaSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IOptions<QuotaSettings>>()
            .Value);

        foreach (var type in GetLoadableTypes(typeof(CpuQuota).Assembly))
        {
            if (type is not { IsClass: true, IsAbstract: false })
                continue;

            if (typeof(Quota).IsAssignableFrom(type))
            {
                services.TryAddSingleton(type);
                services.AddSingleton(
                    typeof(Quota),
                    serviceProvider => (Quota)serviceProvider.GetRequiredService(type));
            }

            if (typeof(IQuotaProfile).IsAssignableFrom(type))
            {
                services.TryAddSingleton(type);
                services.AddSingleton(
                    typeof(IQuotaProfile),
                    serviceProvider => (IQuotaProfile)serviceProvider.GetRequiredService(type));
            }
        }

        services.TryAddSingleton<QuotaCatalog>();
        services.AddQuotaPersistence();
        services.TryAddScoped<IQuotaUsageRelease, QuotaUsageRelease>();
        services.TryAddScoped<IQuotaAdmissionExecutor, QuotaAdmissionExecutor>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, QuotaCatalogStartupService>());

        return services;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    private sealed class QuotaCatalogStartupService(QuotaCatalog catalog) : IHostedService
    {
        private readonly QuotaCatalog catalog = catalog;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = catalog.Definitions.Count;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
