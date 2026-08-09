using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.ResourceDeployments.Application.Placement;

public static class ResourceDeploymentPlacementRegistration
{
    public static IServiceCollection AddResourceDeploymentPlacementCapabilities(
        this IServiceCollection services)
    {
        services.TryAddScoped<ResourceDeploymentPlacementResolver>();
        services.TryAddScoped<ResolveResourceDeploymentTarget>(serviceProvider =>
            serviceProvider
                .GetRequiredService<ResourceDeploymentPlacementResolver>()
                .InvokeAsync);

        services.TryAddScoped<ResourceDeploymentWriter>();
        services.TryAddScoped<CreateResourceDeployment>(serviceProvider =>
            serviceProvider
                .GetRequiredService<ResourceDeploymentWriter>()
                .InvokeAsync);

        return services;
    }
}
