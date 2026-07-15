using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.Quotas.Application.Services;
using Pole.Features.Quotas.Contracts;

namespace Pole.Features.Quotas.Application;

public static class QuotaServiceCollectionExtensions
{
    public static IServiceCollection AddQuotaFramework(
        this IServiceCollection services,
        params Assembly[] featureAssemblies)
    {
        var assemblies = featureAssemblies
            .Append(typeof(CpuQuota).Assembly)
            .Distinct()
            .ToArray();

        foreach (var type in assemblies.SelectMany(GetLoadableTypes))
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
        services.TryAddScoped<IQuotaAdmissionService, MongoQuotaAdmissionService>();
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
