using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Mutations;
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
        services.TryAddScoped<CreateResourceDeploymentSet>(serviceProvider =>
            serviceProvider
                .GetRequiredService<ResourceDeploymentWriter>()
                .InvokeSetAsync);
        services.TryAddScoped<CreateResourceDeploymentSetOutcome>(serviceProvider =>
            serviceProvider
                .GetRequiredService<ResourceDeploymentWriter>()
                .InvokeSetOutcomeAsync);
        services.TryAddScoped<ResourceDeploymentMutationWriter>();
        services.TryAddScoped<IResourceDeploymentSetMutationWriter>(
            serviceProvider => serviceProvider
                .GetRequiredService<ResourceDeploymentMutationWriter>());
        services.TryAddScoped<IResourceDeploymentSetOutcomeMutationWriter>(
            serviceProvider => serviceProvider
                .GetRequiredService<ResourceDeploymentMutationWriter>());
        services.TryAddScoped<IResourceDeploymentExactRetryWriter>(
            serviceProvider => serviceProvider
                .GetRequiredService<ResourceDeploymentMutationWriter>());
        services.TryAddScoped<
            IResourceDeploymentSetQueryService,
            ResourceDeploymentQueryService>();
        services.TryAddScoped<
            IResourceDeploymentExactFinalizationService,
            ResourceDeploymentFinalizationService>();

        return services;
    }
}
