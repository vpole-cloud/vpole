using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Application.Common;
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
        services.AddHttpProblemMappings(ComputeProfileContractHttpMappings.All);
        services.AddHttpProblemMappings(ComputeProfileHttpMappings.All);
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

        services.TryAddSingleton<ComputeProfileBootstrapper>();
        services.TryAddSingleton<BootstrapComputeProfiles>(serviceProvider =>
            serviceProvider
                .GetRequiredService<ComputeProfileBootstrapper>()
                .BootstrapAsync);
        return services;
    }
}
