using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Pole.ASP.Helpers;

public static class HttpProblemMappingServiceCollectionExtensions
{
    public static IServiceCollection AddHttpProblemMappings(
        this IServiceCollection services,
        IEnumerable<HttpProblemMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(mappings);
        foreach (var mapping in mappings.ToArray())
        {
            ArgumentNullException.ThrowIfNull(mapping);
            services.AddSingleton(mapping);
        }

        services.TryAddSingleton<HttpProblemMapper>();
        services.TryAddSingleton<IHttpProblemMapper>(serviceProvider =>
            serviceProvider.GetRequiredService<HttpProblemMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, HttpProblemMappingStartupService>());
        return services;
    }

    private sealed class HttpProblemMappingStartupService(
        HttpProblemMapper mapper
    ) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = mapper;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
