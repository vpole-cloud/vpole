using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.Features.ComputeProfiles.Application.Bootstrap;
using Pole.Features.ComputeProfiles.Application.Persistence;
using Pole.Features.ComputeProfiles.Application.Validation;

namespace Pole.Features.ComputeProfiles.Application;

public static class ComputeProfileApplicationRegistration
{
    public static IServiceCollection AddComputeProfiles(
        this IServiceCollection services)
    {
        services.AddComputeProfilePersistence();

        services.AddScoped<KubernetesComputeProfileTargetMappingValidator>();
        services.AddScoped<ValidateComputeProfileTargetMappings>(serviceProvider =>
            serviceProvider
                .GetRequiredService<KubernetesComputeProfileTargetMappingValidator>()
                .InvokeAsync);
        services.AddScoped<MongoComputeProfileTenantAccessValidator>();
        services.AddScoped<ValidateComputeProfileTenantAccess>(serviceProvider =>
            serviceProvider
                .GetRequiredService<MongoComputeProfileTenantAccessValidator>()
                .InvokeAsync);

        services.TryAddSingleton<IComputeProfileBootstrapper, ComputeProfileBootstrapper>();
        return services;
    }
}
